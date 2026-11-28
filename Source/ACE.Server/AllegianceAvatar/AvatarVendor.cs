using System;
using System.Collections.Generic;
using System.Linq;

using log4net;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Entity.AccountVault;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.Avatars;

/// <summary>What a row on the Avatar's vendor panel sells. The wire guid of a listing resolves to one of these at the moment of sale.</summary>
public enum AvatarListingKind
{
    /// <summary>A Private World Event, not announced server-wide.</summary>
    QuietEvent,

    /// <summary>A Private World Event, announced server-wide.</summary>
    AnnouncedEvent,

    /// <summary>An Allegiance Gauntlet run.</summary>
    Gauntlet,

    /// <summary>The Allegiance Blessing: bonus time added to every online member's offline bonus bank.</summary>
    Blessing,
}

/// <summary>
/// The Allegiance Avatar NPC: the vendor class WorldObjectFactory builds for a weenie carrying
/// PropertyBool.AllegianceAvatarVendor (9098), ahead of the WeenieType switch. The weenie stays type 10 (Creature), so an
/// image without the factory arm builds a plain Creature that sells nothing, never a retail Vendor.
///
/// It sells Private World Events (quiet and announced), the Allegiance Gauntlet and the Allegiance Blessing, paid for in the
/// Avatar's Favor, to a member of the bound allegiance who may spend (<see cref="AvatarRights.CanSpend(Player, AvatarState)"/>) while the Avatar is Bound and Active. The shape follows
/// ThreadPeddlerVendor: per-viewer listings, a virtual balance, an inert stock/sell path, and a buy that never calls base,
/// so FinalizeBuyTransaction never runs and nothing reaches a pack.
/// </summary>
public class AvatarVendor : Vendor, IAvatarFigure
{
    /// <summary>The Avatar NPC weenie.</summary>
    public const uint AvatarWcid = 1008500;

    /// <summary>The Avatar's alternate currency weenie (never created as an object).</summary>
    public const uint FavorWcid = 1008501;

    /// <summary>The display-only shell for a listed Private World Event.</summary>
    public const uint EventListingWcid = 1008502;

    /// <summary>The display-only shell for the Allegiance Gauntlet row.</summary>
    public const uint GauntletListingWcid = 1008503;

    /// <summary>The display-only shell for the Allegiance Blessing row.</summary>
    public const uint BlessingListingWcid = 1008508;

    public const string BlessingName = "Allegiance Blessing";

    public const string GauntletName = "Allegiance Gauntlet";
    public const string QuietName = "Private World Event (quiet)";
    public const string AnnouncedName = "Private World Event (announced)";
    public const string OtherAllegianceMessage = "This Avatar serves another allegiance.";
    public const string NotAllowedMessage = "Only the Monarch, the Keeper or a Seneschal of this Avatar's allegiance may spend its Favor.";
    public const string NotActiveMessage = "This Avatar is not active.";
    public const string NotForSaleMessage = "That is not for sale here.";
    public const string OneAtATimeMessage = "Buy one Private World Event at a time.";
    public const string BuysNothingMessage = "The Avatar buys nothing.";
    public const string ClosedMessage = "The Avatar is not open for business.";

    /// <summary>The buy hand-off. Defaults to <see cref="AvatarEventPurchase.TryBuy"/>; tests capture what the vendor passes.</summary>
    internal delegate bool BuyHandlerFn(Player buyer, AvatarVendor npc, bool announced, out string refusalLine);

    internal static BuyHandlerFn BuyHandler = AvatarEventPurchase.TryBuy;

    /// <summary>The Gauntlet buy hand-off. Defaults to <see cref="Gauntlet.GauntletPurchase.TryBuy"/>; tests capture what the vendor passes.</summary>
    internal delegate bool GauntletBuyHandlerFn(Player buyer, AvatarVendor npc, out string refusalLine);

    internal static GauntletBuyHandlerFn GauntletBuyHandler = Gauntlet.GauntletPurchase.TryBuy;

    /// <summary>The Blessing buy hand-off. Defaults to <see cref="AvatarBlessing.TryBuy"/>; tests capture what the vendor passes.</summary>
    internal static GauntletBuyHandlerFn BlessingBuyHandler = AvatarBlessing.TryBuy;

    /// <summary>Test seam: builds the Blessing listing shell. Defaults to a new object from <see cref="BlessingListingWcid"/>.</summary>
    internal static Func<WorldObject> BlessingListingBuilder = DefaultBlessingListingBuilder;

    /// <summary>Test seam: builds the Gauntlet listing shell. Defaults to a new object from <see cref="GauntletListingWcid"/>.</summary>
    internal static Func<WorldObject> GauntletListingBuilder = DefaultGauntletListingBuilder;

    /// <summary>Test seam: the live tunables. Defaults to <see cref="AvatarTuning.FromPropertyManager"/>.</summary>
    internal static Func<AvatarTuning> Tuning = AvatarTuning.FromPropertyManager;

    /// <summary>Test seam: builds one listing shell. Defaults to a new object from <see cref="EventListingWcid"/>.</summary>
    internal static Func<WorldObject> ListingBuilder = DefaultListingBuilder;

    /// <summary>Test seam: opens the panel after the status lines. Null means base.ActOnUse.</summary>
    internal Action<Player> PanelOpener;

    /// <summary>Test seam: every line sent to a player by this vendor.</summary>
    internal Action<Player, string> Told;

    internal static void ResetSeams()
    {
        BuyHandler = AvatarEventPurchase.TryBuy;
        GauntletBuyHandler = Gauntlet.GauntletPurchase.TryBuy;
        GauntletListingBuilder = DefaultGauntletListingBuilder;
        BlessingBuyHandler = AvatarBlessing.TryBuy;
        BlessingListingBuilder = DefaultBlessingListingBuilder;
        ListingBuilder = DefaultListingBuilder;
        Tuning = AvatarTuning.FromPropertyManager;
        AccountNames = DefaultAccountNames;
    }

    private static WorldObject DefaultListingBuilder() => WorldObjectFactory.CreateNewWorldObject(EventListingWcid);

    private static WorldObject DefaultGauntletListingBuilder() => WorldObjectFactory.CreateNewWorldObject(GauntletListingWcid);

    private static WorldObject DefaultBlessingListingBuilder() => WorldObjectFactory.CreateNewWorldObject(BlessingListingWcid);

    private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    private readonly object listingGate = new object();
    private readonly Dictionary<uint, AvatarListingKind> kindByWireGuid = new Dictionary<uint, AvatarListingKind>();
    private readonly List<WorldObject> listings = new List<WorldObject>();
    private bool listingsBuilt;
    private WorldObject gauntletListing;
    private WorldObject blessingListing;
    private readonly Dictionary<uint, List<WorldObject>> panelRows = new Dictionary<uint, List<WorldObject>>();

    /// <summary>The hair texture pair (old, new) the dressed look carries, or null. Read by WorldObject.AddBaseModelData, the way a Player reads its Character's.</summary>
    internal (uint, uint)? HairTextureOverride { get; set; }

    (uint, uint)? IAvatarFigure.HairTextureOverride { get => HairTextureOverride; set => HairTextureOverride = value; }

    /// <summary>The Avatar this NPC speaks for. 0 until seated or bound by the lifecycle.</summary>
    public uint AvatarId { get; internal set; }

    /// <summary>
    /// True for the SEATED NPC (set by AvatarNpcs before it enters the world). The seated Avatar is a figure, not a shop:
    /// using it sends the status lines only, because opening the panel rotates the NPC and breaks its held pose. Only the
    /// summoned NPC trades.
    /// </summary>
    public bool IsSeated { get; internal set; }

    public const string SeatedNoTradeMessage = "The seated Avatar holds its pose and does not trade. Use /avatar summon to spend Favor.";

    /// <summary>Called by the factory when a weenie is created.</summary>
    public AvatarVendor(Weenie weenie, ObjectGuid guid) : base(weenie, guid)
    {
    }

    /// <summary>Called by the factory when a persisted biota is loaded.</summary>
    public AvatarVendor(Biota biota) : base(biota)
    {
    }

    private AvatarState Avatar => AvatarId == 0 ? null : AvatarManager.Get(AvatarId);

    internal static bool IsLive(AvatarState a) => a != null && a.State == AvatarBindState.Bound && a.Active;

    #region Inert stock and sell path

    protected override void LoadInventory()
    {
    }

    protected override void RotUniques()
    {
    }

    protected override void PrepareResetToHome()
    {
    }

    public override uint GetSellCost(WorldObject item) => 0;

    public override uint GetSellCost(Weenie item) => 0;

    public override int GetBuyCost(WorldObject item) => 0;

    public override int GetBuyCost(Weenie item) => 0;

    public override int CalculatePayoutCoinAmount(Dictionary<uint, WorldObject> items) => 0;

    public override void AddDefaultItem(WorldObject item)
    {
        log.Error($"[AVATAR] {Name}: refused to add 0x{item?.Guid.Full ?? 0:X8} ({item?.Name}) through AddDefaultItem. Listings are built by the vendor itself.");
    }

    public override bool CanAccept(WorldObject wo, Player player, out string reason)
    {
        reason = BuysNothingMessage;
        return false;
    }

    public override bool CanAccept(WorldObject wo, Player player, VaultAccess resolvedAccess, out string reason)
    {
        reason = BuysNothingMessage;
        return false;
    }

    public override bool TryValidateSellBasket(Player player, IReadOnlyList<WorldObject> requested, IReadOnlyList<WorldObject> accepted, out string reason)
    {
        reason = BuysNothingMessage;
        return false;
    }

    public override void ProcessItemsForPurchase(Player player, Dictionary<uint, WorldObject> items)
    {
        log.Error($"[AVATAR] {Name}: refused ProcessItemsForPurchase of {items?.Count ?? 0} item(s) from {player?.Name}. The Avatar buys nothing.");
    }

    #endregion

    #region Listings

    /// <summary>The two Private World Event rows, in panel order. The Gauntlet row is built separately (<see cref="EnsureGauntletListing"/>).</summary>
    private static readonly (string Name, AvatarListingKind Kind)[] EventRows =
    {
        (QuietName, AvatarListingKind.QuietEvent),
        (AnnouncedName, AvatarListingKind.AnnouncedEvent),
    };

    /// <summary>Builds the two event display rows once. False when the listing shell cannot be built (the panel then stays empty).</summary>
    private bool EnsureListings()
    {
        lock (listingGate)
        {
            if (listingsBuilt)
                return listings.Count == EventRows.Length;

            try
            {
                foreach (var (name, kind) in EventRows)
                {
                    var display = ListingBuilder();
                    if (display == null)
                        throw new InvalidOperationException($"the listing shell (wcid {EventListingWcid}) could not be built");

                    display.Name = name;
                    display.ContainerId = Guid.Full;
                    display.VendorShopCreateListStackSize = -1;
                    listings.Add(display);
                    kindByWireGuid[GetWireGuid(display)] = kind;
                }

                listingsBuilt = true;
            }
            catch (Exception ex)
            {
                log.Error($"[AVATAR] {Name}: could not build the event listings; the panel stays empty.", ex);

                foreach (var built in listings)
                {
                    try
                    {
                        built.Destroy();
                    }
                    catch (Exception destroyEx)
                    {
                        log.Error($"[AVATAR] {Name}: could not destroy a half-built listing.", destroyEx);
                    }
                }

                listings.Clear();
                kindByWireGuid.Clear();
                listingsBuilt = false;
                return false;
            }

            return listings.Count == EventRows.Length;
        }
    }

    /// <summary>
    /// Builds the Gauntlet row once, apart from the event rows so a failed Gauntlet shell never blanks the two event rows.
    /// False when it cannot be built (the row is then simply absent; the build is retried on the next panel).
    /// </summary>
    private bool EnsureGauntletListing()
    {
        lock (listingGate)
        {
            if (gauntletListing != null)
            {
                // The event-row build failure path clears the whole kind map; re-register so the row stays buyable.
                kindByWireGuid[GetWireGuid(gauntletListing)] = AvatarListingKind.Gauntlet;
                return true;
            }

            try
            {
                var display = GauntletListingBuilder();
                if (display == null)
                    throw new InvalidOperationException($"the Gauntlet listing shell (wcid {GauntletListingWcid}) could not be built");

                display.Name = GauntletName;
                display.ContainerId = Guid.Full;
                display.VendorShopCreateListStackSize = -1;
                gauntletListing = display;
                kindByWireGuid[GetWireGuid(display)] = AvatarListingKind.Gauntlet;
                return true;
            }
            catch (Exception ex)
            {
                log.Error($"[AVATAR] {Name}: could not build the Gauntlet listing; the row stays hidden.", ex);
                return false;
            }
        }
    }

    /// <summary>
    /// Builds the Blessing row once, apart from the other rows so a failed shell never blanks them. False when it cannot be
    /// built (the row is then absent; the build is retried on the next panel).
    /// </summary>
    private bool EnsureBlessingListing()
    {
        lock (listingGate)
        {
            if (blessingListing != null)
            {
                // The event-row build failure path clears the whole kind map; re-register so the row stays buyable.
                kindByWireGuid[GetWireGuid(blessingListing)] = AvatarListingKind.Blessing;
                return true;
            }

            try
            {
                var display = BlessingListingBuilder();
                if (display == null)
                    throw new InvalidOperationException($"the Blessing listing shell (wcid {BlessingListingWcid}) could not be built");

                display.Name = BlessingName;
                display.ContainerId = Guid.Full;
                display.VendorShopCreateListStackSize = -1;
                blessingListing = display;
                kindByWireGuid[GetWireGuid(display)] = AvatarListingKind.Blessing;
                return true;
            }
            catch (Exception ex)
            {
                log.Error($"[AVATAR] {Name}: could not build the Blessing listing; the row stays hidden.", ex);
                return false;
            }
        }
    }

    /// <summary>
    /// Whether the Gauntlet row is offered at all: the run manager has installed its starter (a purchase with no starter can
    /// only refuse) and the Gauntlet switch is on.
    /// </summary>
    private static bool GauntletOffered() => Gauntlet.GauntletRunStarter.Current != null && Gauntlet.GauntletPurchase.Tuning().Enabled;

    /// <summary>
    /// The rows the viewer sees: the event listings, the Gauntlet row while it is offered, then the Blessing row priced for
    /// the members online now, for a viewer who may spend while the Avatar is Bound and Active; else none.
    /// </summary>
    private List<WorldObject> RowsFor(Player viewer)
    {
        var rows = new List<WorldObject>();

        var avatar = Avatar;
        if (viewer == null || !Tuning().Enabled || !IsLive(avatar) || !AvatarRights.CanSpend(viewer, avatar) || !EnsureListings())
            return rows;

        var t = Tuning();
        var (rosterAccounts, serverAccounts) = AvatarManager.PriceInputs(avatar);

        lock (listingGate)
        {
            listings[0].Value = (int)Math.Min(AvatarPricing.Quiet(t, rosterAccounts, serverAccounts), int.MaxValue);
            listings[1].Value = (int)Math.Min(AvatarPricing.Announced(t, rosterAccounts, serverAccounts), int.MaxValue);
            rows.AddRange(listings);
        }

        // The viewer already passed CanSpend above, so the Gauntlet row shows to spenders only.
        if (GauntletOffered() && EnsureGauntletListing())
        {
            lock (listingGate)
            {
                gauntletListing.Value = (int)Math.Min(AvatarPricing.Gauntlet(t, Gauntlet.GauntletPurchase.Tuning(), rosterAccounts, serverAccounts), int.MaxValue);
                rows.Add(gauntletListing);
            }
        }

        // Priced outside the listing lock: the price reads the online roster.
        var blessingPrice = AvatarBlessing.CurrentPrice(avatar, t);
        if (EnsureBlessingListing())
        {
            lock (listingGate)
            {
                blessingListing.Value = (int)Math.Min(blessingPrice, int.MaxValue);
                rows.Add(blessingListing);
            }
        }

        return rows;
    }

    /// <summary>The viewer's rows from the panel snapshot when one is being written, else a fresh read.</summary>
    private List<WorldObject> PanelRowsFor(Player viewer)
    {
        if (viewer != null)
        {
            lock (listingGate)
            {
                if (panelRows.TryGetValue(viewer.Guid.Full, out var snap))
                    return snap;
            }
        }

        return RowsFor(viewer);
    }

    /// <summary>One snapshot of the viewer's rows for the duration of <paramref name="body"/>, so the count field and the rows of one panel message agree.</summary>
    internal void WithPanelSnapshot(Player viewer, Action body)
    {
        if (viewer == null)
        {
            body();
            return;
        }

        var guid = viewer.Guid.Full;
        var rows = RowsFor(viewer);

        lock (listingGate)
            panelRows[guid] = rows;

        try
        {
            body();
        }
        finally
        {
            lock (listingGate)
                panelRows.Remove(guid);
        }
    }

    /// <summary>Every panel message (Use, the buy refresh, the refused-buy re-send) reads one row snapshot.</summary>
    /// The seated figure never rotates or sends a panel, including the re-send Player.HandleActionBuyItem makes after a
    /// refused buy.
    public override void ApproachVendor(Player player, VendorType action = VendorType.Undef, uint altCurrencySpent = 0)
    {
        if (IsSeated)
            return;

        WithPanelSnapshot(player, () => base.ApproachVendor(player, action, altCurrencySpent));
    }

    public override void ForEachItemFor(Player viewer, Action<WorldObject> action)
    {
        foreach (var display in PanelRowsFor(viewer))
            action(display);
    }

    public override int GetItemsForSaleCount(Player viewer) => PanelRowsFor(viewer).Count;

    public override bool TryGetItemForSale(Player viewer, ObjectGuid itemGuid, out WorldObject itemForSale)
    {
        itemForSale = PanelRowsFor(viewer).FirstOrDefault(d => d.Guid == itemGuid);
        return itemForSale != null;
    }

    /// <summary>Fails closed: with no viewer there is nobody whose rights could filter the panel.</summary>
    public override void forEachItem(Action<WorldObject> action)
    {
    }

    public override int ItemsForSaleCount => 0;

    #endregion

    #region Currency

    /// <summary>The panel's balance: the Avatar's spendable Favor for the Favor currency, to a member; 0 for anything else. Never an inventory read.</summary>
    public override long AlternateCurrencyBalanceFor(Player viewer, uint wcid)
    {
        if (viewer == null || wcid != FavorWcid)
            return 0;

        var avatar = Avatar;
        if (avatar == null || !AvatarRights.IsMember(viewer, avatar))
            return 0;

        return (long)Math.Min(avatar.AvailableFavor, (ulong)long.MaxValue);
    }

    #endregion

    #region Use

    /// <summary>A member sees the status lines, then the panel; anyone else is told the Avatar serves another allegiance.</summary>
    public override void ActOnUse(WorldObject wo)
    {
        if (!(wo is Player player))
            return;

        // As ThreadPeddlerVendor: Vendor's constructor sets OpenForBusiness from ValidateVendorRequirements, so a weenie
        // missing its vendor rows (or the Favor currency weenie) is closed and opens nothing.
        if (!OpenForBusiness)
        {
            Tell(player, ClosedMessage);
            return;
        }

        if (player.IsBusy)
        {
            player.SendWeenieError(WeenieError.YoureTooBusy);
            return;
        }

        var avatar = Avatar;
        if (avatar == null || !AvatarRights.IsMember(player, avatar))
        {
            Tell(player, OtherAllegianceMessage);
            return;
        }

        var statusTuning = Tuning();

        foreach (var line in AvatarStatusText.Lines(avatar, statusTuning, AvatarManager.FavorCapFor(avatar, statusTuning), AccountDisplayName))
            Tell(player, line);

        // The seated figure never rotates or opens the panel (base.ActOnUse does both), so its pose holds.
        if (IsSeated)
        {
            Tell(player, SeatedNoTradeMessage);
            return;
        }

        if (PanelOpener != null)
            PanelOpener(player);
        else
            base.ActOnUse(wo);
    }

    /// <summary>Test seam: the character names on an account. Defaults to the in-memory PlayerManager snapshot.</summary>
    internal static Func<uint, IEnumerable<string>> AccountNames = DefaultAccountNames;

    private static IEnumerable<string> DefaultAccountNames(uint accountId) => PlayerManager.GetAccountPlayersSnapshot(accountId)?.Select(p => p.Name);

    /// <summary>An account's display name: its alphabetically first character name (case-insensitive), or null.</summary>
    internal static string AccountDisplayName(uint accountId) =>
        AccountNames(accountId)?.Where(n => !string.IsNullOrEmpty(n)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).FirstOrDefault();

    internal void Tell(Player player, string text)
    {
        if (player == null || string.IsNullOrEmpty(text))
            return;

        Told?.Invoke(player, text);

        player.Session?.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));
    }

    #endregion

    #region Buy

    /// <summary>
    /// The buy. Never calls base, so FinalizeBuyTransaction never runs and nothing reaches the pack. Membership, rights, Active
    /// and the binding are checked here, at the moment of sale, never trusted from the panel that was shown earlier; the one
    /// requested row is then resolved through the listing map and handed to <see cref="AvatarEventPurchase.TryBuy"/>.
    /// A refusal sends its line and returns false (Player.HandleActionBuyItem then re-sends the panel).
    /// </summary>
    public override bool BuyItems_ValidateTransaction(List<ItemProfile> itemProfiles, Player player)
    {
        if (player == null)
            return false;

        // The seated figure never shows a panel, so a buy naming it is stale or forged.
        if (IsSeated)
        {
            Tell(player, SeatedNoTradeMessage);
            return false;
        }

        var avatar = Avatar;
        if (avatar == null || !AvatarRights.IsMember(player, avatar))
        {
            Tell(player, OtherAllegianceMessage);
            return false;
        }

        if (!IsLive(avatar))
        {
            Tell(player, NotActiveMessage);
            return false;
        }

        if (!AvatarRights.CanSpend(player, avatar))
        {
            Tell(player, NotAllowedMessage);
            return false;
        }

        if (itemProfiles == null || itemProfiles.Count != 1)
        {
            Tell(player, itemProfiles != null && itemProfiles.Count > 1 ? OneAtATimeMessage : NotForSaleMessage);
            return false;
        }

        var profile = itemProfiles[0];
        AvatarListingKind kind;
        bool known;
        lock (listingGate)
            known = kindByWireGuid.TryGetValue(profile.ObjectGuid, out kind);

        if (!known || profile.Amount != 1)
        {
            Tell(player, known ? OneAtATimeMessage : NotForSaleMessage);
            return false;
        }

        string line;
        var bought = kind switch
        {
            AvatarListingKind.Gauntlet => GauntletBuyHandler(player, this, out line),
            AvatarListingKind.Blessing => BlessingBuyHandler(player, this, out line),
            _ => BuyHandler(player, this, kind == AvatarListingKind.AnnouncedEvent, out line),
        };

        if (!bought)
        {
            Tell(player, line);
            return false;
        }

        return true;
    }

    #endregion
}
