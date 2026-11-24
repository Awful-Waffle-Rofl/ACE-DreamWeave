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
    Indoors,
    InThreadRun,
    SlotBusy,
    ScheduleSoon,
    InsufficientFavor,
    StartFailed,
}

/// <summary>
/// The Private World Event purchase. <see cref="Check"/> is the pure rule set and runs every refusal before any Favor moves;
/// <see cref="TryBuy"/> reserves the price, enqueues the start onto the world thread, then commits on a start and releases on
/// a failure. The vendor has already re-checked membership, rights, Active and the binding; TryBuy re-checks for itself.
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
    }

    /// <summary>
    /// Settles a private event's reserved Favor: <paramref name="commit"/> spends it (the run reached Active), otherwise it is
    /// returned (the run ended before Active). Called by the run itself through WorldEventHooks.
    /// </summary>
    public static void Settle(uint avatarId, ulong price, bool commit)
    {
        if (commit)
            AvatarManager.CommitDebit(avatarId, price);
        else
            AvatarManager.ReleaseReservation(avatarId, price);
    }

    /// <summary>
    /// The pure rule set, in the order the buyer would most want to hear about it. A scheduled event within
    /// EventScheduleBufferMinutes (inclusive) of now refuses, so a private event never runs into a public one.
    /// </summary>
    public static AvatarEventRefusal Check(bool permitted, bool active, int level, Position spot, bool inThreadRun, bool currentRunning,
        DateTime? nextScheduledUtc, DateTime nowUtc, ulong available, ulong price, AvatarTuning t)
    {
        if (!permitted)
            return AvatarEventRefusal.NotPermitted;

        if (!active)
            return AvatarEventRefusal.Inactive;

        if (level < t.EventMinLevel)
            return AvatarEventRefusal.LevelTooLow;

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

    /// <summary>One ASCII line for the buyer.</summary>
    public static string LineFor(AvatarEventRefusal refusal, AvatarTuning t, string detail = null) => refusal switch
    {
        AvatarEventRefusal.NotPermitted => "You may not spend this Avatar's Favor.",
        AvatarEventRefusal.Inactive => "The Avatar is not active right now.",
        AvatarEventRefusal.LevelTooLow => $"The Avatar must reach level {t.EventMinLevel} before it can call a World Event.",
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
        var price = (ulong)Math.Max(0, announced ? t.EventPriceAnnounced : t.EventPriceQuiet);

        var refusal = Check(permitted, active, avatar?.Level(t) ?? 0, npc.Location, InThreadRun(buyer), CurrentRunning(),
            NextScheduledUtc(), UtcNow(), avatar?.AvailableFavor ?? 0, price, t);

        if (refusal != AvatarEventRefusal.None)
        {
            refusalLine = LineFor(refusal, t);
            return false;
        }

        var avatarId = avatar.AvatarId;

        if (!AvatarManager.TryReserveFavor(avatarId, price))
        {
            refusalLine = LineFor(AvatarEventRefusal.InsufficientFavor, t);
            return false;
        }

        var request = new WorldEventRequest
        {
            Random = true,
            AnchorPosition = new Position(npc.Location),
            AnchorLabel = WorldEventTownIndex.Describe(npc.Location),
            Invoker = $"avatar:{avatarId}:{buyer.Name}",
            AvatarId = avatarId,
            AnnounceServerWide = announced,
            AvatarFavorPrice = price,
        };

        var label = request.AnchorLabel;
        var buyerName = buyer.Name;

        // Reserve, then settle with the run: the reservation already drops Favor - ReservedFavor, so the panel re-sent now shows
        // the balance a commit will leave (CommitDebit lowers both terms by the price). The run commits when it first reaches
        // Active and releases if it ends before that (WorldEvent.SettleAvatarFavor); reservations are memory only, so a restart
        // before Active releases implicitly.
        WorldEnqueue(new ActionEventDelegate(() => StartOnWorldThread(buyer, npc, request, avatarId, price, buyerName, label)));

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

            // The run took hold before the throw: it settles its own Favor, so nothing is released here.
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
