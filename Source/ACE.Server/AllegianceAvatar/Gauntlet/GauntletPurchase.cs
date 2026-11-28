using System;

using log4net;

using ACE.Entity;
using ACE.Server.Managers;
using ACE.Server.Realms;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldObjects;

namespace ACE.Server.Avatars.Gauntlet;

public enum GauntletRefusal
{
    None,
    NotPermitted,
    Inactive,
    LevelTooLow,
    InInstance,
    Disabled,
    InsufficientFavor,
    NotAvailable,
    StartRefused,
}

/// <summary>
/// The Allegiance Gauntlet purchase. <see cref="Check"/> is the pure rule set; <see cref="TryBuy"/> runs it, asks the run
/// manager whether a run may start (<see cref="IGauntletRunStarter.CheckCanStart"/>), reserves the price, then hands the run
/// to the manager. Every refusal comes before any Favor moves. From a true <see cref="IGauntletRunStarter.TryStart"/> the run
/// owns the reservation (commit at wave 1, release on an earlier end); on a false return this class releases it.
/// </summary>
public static class GauntletPurchase
{
    private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    public const string NotAvailableLine = "The Allegiance Gauntlet is not available right now.";

    /// <summary>Test seam: the live Gauntlet tunables. Defaults to <see cref="GauntletTuning.FromPropertyManager"/>.</summary>
    internal static Func<GauntletTuning> Tuning = GauntletTuning.FromPropertyManager;

    /// <summary>Test seam: the clock.</summary>
    internal static Func<DateTime> UtcNow = () => DateTime.UtcNow;

    /// <summary>Test seam: whether the selling NPC stands in an ephemeral instance or a Thread run.</summary>
    internal static Func<Position, bool> NpcInInstance = DefaultNpcInInstance;

    /// <summary>Test seam: re-sends the vendor panel so the client shows the current Favor balance.</summary>
    internal static Action<AvatarVendor, Player> ResendPanel = DefaultResendPanel;

    private static bool DefaultNpcInInstance(Position p)
    {
        if (p == null)
            return true;

        return EphemeralInstanceGroup.IsEphemeralInstance(p.Instance) || (p.Instance != 0 && ThreadDungeonManager.GetRun(p.Instance) != null);
    }

    private static void DefaultResendPanel(AvatarVendor npc, Player p) => npc.ApproachVendor(p, ACE.Entity.Enum.VendorType.Undef);

    internal static void ResetSeams()
    {
        Tuning = GauntletTuning.FromPropertyManager;
        UtcNow = () => DateTime.UtcNow;
        NpcInInstance = DefaultNpcInInstance;
        ResendPanel = DefaultResendPanel;
    }

    /// <summary>
    /// The pure rule set, in the spec's order: rights, Bound and Active, the Avatar level, the NPC's place, the master switch,
    /// then Favor. The checks that need run state (already running, cooldown, server cap, no arena) are the starter's.
    /// </summary>
    public static GauntletRefusal Check(bool permitted, bool active, int avatarLevel, bool npcInInstance, bool available, ulong favor, ulong price, GauntletTuning t)
    {
        if (!permitted)
            return GauntletRefusal.NotPermitted;

        if (!active)
            return GauntletRefusal.Inactive;

        if (avatarLevel < t.MinAvatarLevel)
            return GauntletRefusal.LevelTooLow;

        if (npcInInstance)
            return GauntletRefusal.InInstance;

        if (!available)
            return GauntletRefusal.Disabled;

        if (favor < price)
            return GauntletRefusal.InsufficientFavor;

        return GauntletRefusal.None;
    }

    /// <summary>One ASCII line for the buyer.</summary>
    public static string LineFor(GauntletRefusal refusal, GauntletTuning t, string detail = null) => refusal switch
    {
        GauntletRefusal.NotPermitted => "You may not spend this Avatar's Favor.",
        GauntletRefusal.Inactive => "The Avatar is not active right now.",
        GauntletRefusal.LevelTooLow => $"The Avatar must reach level {t.MinAvatarLevel} before it can call an Allegiance Gauntlet.",
        GauntletRefusal.InInstance => "An Allegiance Gauntlet cannot be called from inside an instance or a Thread dungeon.",
        GauntletRefusal.Disabled => "The Allegiance Gauntlet is switched off.",
        GauntletRefusal.InsufficientFavor => "The Avatar does not have enough Favor.",
        GauntletRefusal.NotAvailable => NotAvailableLine,
        GauntletRefusal.StartRefused => string.IsNullOrEmpty(detail) ? "The Allegiance Gauntlet could not be started. No Favor was spent." : $"{detail} No Favor was spent.",
        _ => "The Allegiance Gauntlet could not be called.",
    };

    public static bool TryBuy(Player buyer, AvatarVendor npc, out string refusalLine)
    {
        var at = AvatarVendor.Tuning();

        if (!at.Enabled)
        {
            refusalLine = AvatarSeatRules.SwitchedOffMessage;
            return false;
        }

        var t = Tuning();

        if (buyer == null || npc == null)
        {
            refusalLine = LineFor(GauntletRefusal.NotPermitted, t);
            return false;
        }

        var avatar = npc.AvatarId == 0 ? null : AvatarManager.Get(npc.AvatarId);

        var permitted = avatar != null && AvatarRights.IsMember(buyer, avatar) && AvatarRights.CanSpend(buyer, avatar);
        var active = avatar != null && avatar.State == AvatarBindState.Bound && avatar.Active;
        // The price reserved here is what the run commits at wave 1 or releases; it is never recomputed later.
        var (rosterAccounts, serverAccounts) = AvatarManager.PriceInputs(avatar);
        var price = AvatarPricing.Gauntlet(at, t, rosterAccounts, serverAccounts);

        var refusal = Check(permitted, active, avatar?.Level(at) ?? 0, NpcInInstance(npc.Location), t.Enabled, avatar?.AvailableFavor ?? 0, price, t);

        if (refusal != GauntletRefusal.None)
        {
            refusalLine = LineFor(refusal, t);
            return false;
        }

        var starter = GauntletRunStarter.Current;
        if (starter == null)
        {
            refusalLine = NotAvailableLine;
            return false;
        }

        var now = UtcNow();
        var avatarId = avatar.AvatarId;

        var stateRefusal = starter.CheckCanStart(avatarId, now);
        if (stateRefusal != null)
        {
            refusalLine = stateRefusal;
            return false;
        }

        if (!AvatarManager.TryReserveFavor(avatarId, price))
        {
            refusalLine = LineFor(GauntletRefusal.InsufficientFavor, t);
            return false;
        }

        var request = new GauntletStartRequest
        {
            AvatarId = avatarId,
            MonarchGuid = avatar.MonarchGuid,
            BuyerGuid = buyer.Guid.Full,
            NpcLocation = new Position(npc.Location),
            Price = price,
            NowUtc = now,
        };

        bool started;
        string startRefusal = null;

        try
        {
            started = starter.TryStart(request, out startRefusal);
        }
        catch (Exception ex)
        {
            // A throw is treated as "nothing started": the interface gives the reservation to the run only from a true return.
            log.Error($"[GAUNTLET] The run starter threw for Avatar {avatarId}", ex);
            started = false;
            startRefusal = "An internal error stopped the run.";
        }

        if (!started)
        {
            AvatarManager.ReleaseReservation(avatarId, price);
            refusalLine = LineFor(GauntletRefusal.StartRefused, t, startRefusal);
            return false;
        }

        refusalLine = null;
        npc.Tell(buyer, "The Avatar hears your call. An Allegiance Gauntlet is being prepared. The Favor is spent only if it begins.");

        try
        {
            ResendPanel(npc, buyer);
        }
        catch (Exception ex)
        {
            log.Warn("[GAUNTLET] Could not re-send the vendor panel after a Gauntlet purchase", ex);
        }

        return true;
    }
}
