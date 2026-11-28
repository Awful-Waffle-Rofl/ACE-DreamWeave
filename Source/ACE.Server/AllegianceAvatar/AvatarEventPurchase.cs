using System;

using log4net;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Entity.Actions;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldEvents;
using ACE.Server.WorldObjects;
using ACE.Server.Managers;

namespace ACE.Server.Avatars;

public enum AvatarEventRefusal
{
    None,
    NotPermitted,
    Inactive,
    LevelTooLow,
    DailyLimit,
    Indoors,
    InThreadRun,
    SlotBusy,
    ScheduleSoon,
    InsufficientFavor,
    StartFailed,
}

/// <summary>
/// The Private World Event purchase. <see cref="Check"/> is the pure rule set and runs every refusal before any Favor moves;
/// <see cref="TryBuy"/> claims a daily slot, reserves the price, enqueues the start onto the world thread, then commits on a
/// start and releases on a failure. The vendor has already re-checked membership, rights, Active and the binding; TryBuy
/// re-checks for itself.
///
/// DAILY LIMIT (allegiance_avatar_event_daily_limit, quiet and announced share it). The order is claim -> reserve -> start
/// -> commit both or release both: a failed reserve and a failed start give the claim back here, and the run itself settles
/// the rest through <see cref="Settle"/> (both commit when it first reaches Active; both release if it ends before that).
/// Only reaching Active uses the slot.
/// </summary>
public static class AvatarEventPurchase
{
    private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    /// <summary>Test seam: starts the run. Defaults to <see cref="WorldEventHooks.TryStartPrivate"/>.</summary>
    internal static Func<WorldEventRequest, (bool ok, string error)> StartSeam = DefaultStart;

    /// <summary>Test seam: queues work for the world thread. Defaults to <see cref="WorldManager.EnqueueAction"/>.</summary>
    internal static Action<IAction> WorldEnqueue = WorldManager.EnqueueAction;

    /// <summary>Test seam: true while a World Event is running (WorldEventHooks.IsEventRunning, the TryStart refusal's own test).</summary>
    internal static Func<bool> CurrentRunning = WorldEventHooks.IsEventRunning;

    /// <summary>Test seam: the next scheduled occurrence (UTC), or null with the auto scheduler off.</summary>
    internal static Func<DateTime?> NextScheduledUtc = DefaultNextScheduled;

    /// <summary>Test seam: the clock.</summary>
    internal static Func<DateTime> UtcNow = () => DateTime.UtcNow;

    /// <summary>Test seam: whether the buyer stands inside a Thread dungeon run (the lookup AvatarFeed uses).</summary>
    internal static Func<Player, bool> InThreadRun = DefaultInThreadRun;

    /// <summary>Test seam: re-sends the vendor panel so the client shows the current Favor balance.</summary>
    internal static Action<AvatarVendor, Player> ResendPanel = DefaultResendPanel;

    /// <summary>Test seam: whether the request is the run that is Current now.</summary>
    internal static Func<WorldEventRequest, bool> IsCurrentRequest = WorldEventHooks.IsCurrentRequest;

    /// <summary>Test seam: names the anchor for the announcement. Defaults to <see cref="WorldEventTownIndex.Describe"/>.</summary>
    internal static Func<Position, string> DescribeAnchor = WorldEventTownIndex.Describe;

    /// <summary>Test seam: holds the price. Defaults to <see cref="AvatarManager.TryReserveFavor"/>.</summary>
    internal static Func<uint, ulong, bool> ReserveFavor = AvatarManager.TryReserveFavor;

    private static (bool ok, string error) DefaultStart(WorldEventRequest r) => (WorldEventHooks.TryStartPrivate(r, out var e), e);

    private static DateTime? DefaultNextScheduled() => WorldEventScheduler.Live.NextOccurrenceUtc();

    private static bool DefaultInThreadRun(Player p)
    {
        var location = p.Location;
        return location != null && location.Instance != 0 && ThreadDungeonManager.GetRun(location.Instance) != null;
    }

    private static void DefaultResendPanel(AvatarVendor npc, Player p) => npc.ApproachVendor(p, VendorType.Undef);

    internal static void ResetSeams()
    {
        StartSeam = DefaultStart;
        WorldEnqueue = WorldManager.EnqueueAction;
        CurrentRunning = WorldEventHooks.IsEventRunning;
        NextScheduledUtc = DefaultNextScheduled;
        UtcNow = () => DateTime.UtcNow;
        InThreadRun = DefaultInThreadRun;
        ResendPanel = DefaultResendPanel;
        IsCurrentRequest = WorldEventHooks.IsCurrentRequest;
        ReserveFavor = AvatarManager.TryReserveFavor;
        DescribeAnchor = WorldEventTownIndex.Describe;
    }

    /// <summary>
    /// Settles a private event's reserved Favor and its daily claim: <paramref name="commit"/> spends both (the run reached
    /// Active: the price is debited and the day's slot is used), otherwise both are returned (the run ended before Active).
    /// Called by the run itself through WorldEventHooks, exactly once per run, including a run priced at 0.
    /// </summary>
    public static void Settle(uint avatarId, ulong price, bool commit)
    {
        if (commit)
        {
            AvatarManager.CommitDebit(avatarId, price);
            AvatarManager.CommitDaily(avatarId, AvatarDailyKind.Event, AvatarDay.BuildClock(SettleTuning(), UtcNow()));
        }
        else
        {
            AvatarManager.ReleaseReservation(avatarId, price);
            AvatarManager.ReleaseDaily(avatarId, AvatarDailyKind.Event);
        }
    }

    /// <summary>The tunables for the settle's day clock. A failed read falls back to the defaults so the slot is still recorded.</summary>
    private static AvatarTuning SettleTuning()
    {
        try
        {
            return AvatarVendor.Tuning() ?? AvatarTuning.Defaults;
        }
        catch (Exception ex)
        {
            log.Warn("[AVATAR] Reading the Avatar tunables for a private World Event settle failed; using the defaults for the day clock.", ex);
            return AvatarTuning.Defaults;
        }
    }

    /// <summary>
    /// The pure rule set, in the order the buyer would most want to hear about it. A scheduled event within
    /// EventScheduleBufferMinutes (inclusive) of now refuses, so a private event never runs into a public one.
    /// </summary>
    /// <param name="dailyLimitReached">True when the Avatar's private World Events used today plus those in flight already reach
    /// allegiance_avatar_event_daily_limit (never true while the limit is 0).</param>
    public static AvatarEventRefusal Check(bool permitted, bool active, int level, bool dailyLimitReached, Position spot, bool inThreadRun, bool currentRunning,
        DateTime? nextScheduledUtc, DateTime nowUtc, ulong available, ulong price, AvatarTuning t)
    {
        if (!permitted)
            return AvatarEventRefusal.NotPermitted;

        if (!active)
            return AvatarEventRefusal.Inactive;

        if (level < t.EventMinLevel)
            return AvatarEventRefusal.LevelTooLow;

        if (dailyLimitReached)
            return AvatarEventRefusal.DailyLimit;

        if (spot == null || spot.Indoors)
            return AvatarEventRefusal.Indoors;

        if (inThreadRun)
            return AvatarEventRefusal.InThreadRun;

        if (currentRunning)
            return AvatarEventRefusal.SlotBusy;

        if (nextScheduledUtc != null && nextScheduledUtc.Value - nowUtc <= TimeSpan.FromMinutes(t.EventScheduleBufferMinutes))
            return AvatarEventRefusal.ScheduleSoon;

        if (available < price)
            return AvatarEventRefusal.InsufficientFavor;

        return AvatarEventRefusal.None;
    }

    /// <summary>One ASCII line for the buyer. For <see cref="AvatarEventRefusal.DailyLimit"/>, <paramref name="detail"/> is the time until the Avatar day turns over.</summary>
    public static string LineFor(AvatarEventRefusal refusal, AvatarTuning t, string detail = null) => refusal switch
    {
        AvatarEventRefusal.NotPermitted => "You may not spend this Avatar's Favor.",
        AvatarEventRefusal.Inactive => "The Avatar is not active right now.",
        AvatarEventRefusal.LevelTooLow => $"The Avatar must reach level {t.EventMinLevel} before it can call a World Event.",
        AvatarEventRefusal.DailyLimit => string.IsNullOrEmpty(detail)
            ? "Your Avatar has already called a private World Event today. It can call another after the daily reset."
            : $"Your Avatar has already called a private World Event today. It can call another in {detail}.",
        AvatarEventRefusal.Indoors => "A World Event cannot be called here. The Avatar must stand outdoors.",
        AvatarEventRefusal.InThreadRun => "A World Event cannot be called from inside a Thread dungeon.",
        AvatarEventRefusal.SlotBusy => "A World Event is already running. Try again when it ends.",
        AvatarEventRefusal.ScheduleSoon => $"A scheduled World Event begins within {t.EventScheduleBufferMinutes} minutes. Try again afterwards.",
        AvatarEventRefusal.InsufficientFavor => "The Avatar does not have enough Favor.",
        AvatarEventRefusal.StartFailed => string.IsNullOrEmpty(detail) ? "The World Event could not be started. No Favor was spent." : $"The World Event could not be started: {detail}. No Favor was spent.",
        _ => "The World Event could not be called.",
    };

    public static bool TryBuy(Player buyer, AvatarVendor npc, bool announced, out string refusalLine)
    {
        var t = AvatarVendor.Tuning();

        if (!t.Enabled)
        {
            refusalLine = AvatarSeatRules.SwitchedOffMessage;
            return false;
        }

        if (buyer == null || npc == null)
        {
            refusalLine = LineFor(AvatarEventRefusal.NotPermitted, t);
            return false;
        }

        var avatar = npc.AvatarId == 0 ? null : AvatarManager.Get(npc.AvatarId);

        var permitted = avatar != null && AvatarRights.IsMember(buyer, avatar) && AvatarRights.CanSpend(buyer, avatar);
        var active = avatar != null && avatar.State == AvatarBindState.Bound && avatar.Active;
        // The price reserved here is what Settle commits or releases; it is never recomputed later.
        var (rosterAccounts, serverAccounts) = AvatarManager.PriceInputs(avatar);
        var price = announced
            ? AvatarPricing.Announced(t, rosterAccounts, serverAccounts)
            : AvatarPricing.Quiet(t, rosterAccounts, serverAccounts);
        var now = UtcNow();
        var day = AvatarDay.BuildClock(t, now);

        var limitReached = false;
        if (avatar != null && t.EventDailyLimit > 0)
        {
            var (used, pending) = AvatarManager.DailyCount(avatar.AvatarId, AvatarDailyKind.Event, day);
            limitReached = used + pending >= t.EventDailyLimit;
        }

        var refusal = Check(permitted, active, avatar?.Level(t) ?? 0, limitReached, npc.Location, InThreadRun(buyer), CurrentRunning(),
            NextScheduledUtc(), now, avatar?.AvailableFavor ?? 0, price, t);

        if (refusal != AvatarEventRefusal.None)
        {
            refusalLine = LineFor(refusal, t, refusal == AvatarEventRefusal.DailyLimit ? AvatarDay.UntilReset(day) : null);
            return false;
        }

        var avatarId = avatar.AvatarId;

        // The request is built BEFORE anything is claimed or reserved, so a throw here (the anchor copy or the town lookup)
        // leaves nothing held.
        WorldEventRequest request;
        try
        {
            request = new WorldEventRequest
            {
                Random = true,
                AnchorPosition = new Position(npc.Location),
                AnchorLabel = DescribeAnchor(npc.Location),
                Invoker = $"avatar:{avatarId}:{buyer.Name}",
                AvatarId = avatarId,
                AnnounceServerWide = announced,
                AvatarFavorPrice = price,
                AvatarMonarchGuid = avatar?.MonarchGuid,
            };
        }
        catch (Exception ex)
        {
            log.Error($"[AVATAR] Building the private World Event request for Avatar {avatarId} threw; nothing was claimed or reserved.", ex);
            refusalLine = LineFor(AvatarEventRefusal.StartFailed, t);
            return false;
        }

        // Claim -> reserve: the claim is the authoritative limit gate (Check's count was only a read), so a buyer racing for
        // the last slot is refused here with nothing reserved.
        if (!AvatarManager.TryClaimDaily(avatarId, AvatarDailyKind.Event, t.EventDailyLimit, day))
        {
            refusalLine = LineFor(AvatarEventRefusal.DailyLimit, t, AvatarDay.UntilReset(day));
            return false;
        }

        if (!ReserveFavor(avatarId, price))
        {
            AvatarManager.ReleaseDaily(avatarId, AvatarDailyKind.Event);
            refusalLine = LineFor(AvatarEventRefusal.InsufficientFavor, t);
            return false;
        }

        var label = request.AnchorLabel;
        var buyerName = buyer.Name;

        // Reserve, then settle with the run: the reservation already drops Favor - ReservedFavor, so the panel re-sent now shows
        // the balance a commit will leave (CommitDebit lowers both terms by the price). The run commits the price and the daily
        // claim when it first reaches Active and releases both if it ends before that (WorldEvent.SettleAvatarFavor ->
        // Settle); reservations and claims are memory only, so a restart before Active releases both implicitly.
        try
        {
            WorldEnqueue(new ActionEventDelegate(() => StartOnWorldThread(buyer, npc, request, avatarId, price, buyerName, label)));
        }
        catch (Exception ex)
        {
            log.Error($"[AVATAR] Queueing the private World Event start for Avatar {avatarId} threw; the price and the day's slot are released.", ex);
            AvatarManager.ReleaseReservation(avatarId, price);
            AvatarManager.ReleaseDaily(avatarId, AvatarDailyKind.Event);
            refusalLine = LineFor(AvatarEventRefusal.StartFailed, t);
            return false;
        }

        refusalLine = null;
        npc.Tell(buyer, "The Avatar hears your call. A private World Event is being arranged.");
        SafeResend(npc, buyer);
        return true;
    }

    private static void StartOnWorldThread(Player buyer, AvatarVendor npc, WorldEventRequest request, uint avatarId, ulong price, string buyerName, string label)
    {
        bool ok;
        string error;

        try
        {
            (ok, error) = StartSeam(request);
        }
        catch (Exception ex)
        {
            log.Error($"[AVATAR] Private World Event start threw for Avatar {avatarId}", ex);
            ok = false;
            error = "an internal error";

            // The run took hold before the throw: it settles its own Favor and daily claim, so nothing is released here.
            if (IsCurrentRequest(request))
                ok = true;
        }

        if (ok)
        {
            npc.Tell(buyer, "Your private World Event is called. The Favor is spent only if it begins.");

            var monarch = AvatarManager.Get(avatarId)?.MonarchGuid;
            if (monarch != null)
                AvatarAnnouncer.ToAllegiance(monarch.Value, $"{buyerName} has called a private World Event at {label}.");

            return;
        }

        AvatarManager.ReleaseReservation(avatarId, price);
        AvatarManager.ReleaseDaily(avatarId, AvatarDailyKind.Event);
        npc.Tell(buyer, LineFor(AvatarEventRefusal.StartFailed, AvatarVendor.Tuning(), error));
        SafeResend(npc, buyer);
    }

    private static void SafeResend(AvatarVendor npc, Player buyer)
    {
        try
        {
            ResendPanel(npc, buyer);
        }
        catch (Exception ex)
        {
            log.Warn("[AVATAR] Could not re-send the vendor panel after a private World Event purchase", ex);
        }
    }
}
