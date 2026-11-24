using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using log4net;

using ACE.Common;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Entity.AccountVault;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// The Thread Encounters Wandering Peddler (Docs/Threads/ENCOUNTERS.md "Wandering Peddler"): the vendor class
    /// WorldObjectFactory builds for a weenie carrying PropertyBool.ThreadPeddlerVendor (9096), ahead of the WeenieType
    /// switch. The peddler weenie stays type 10 (Creature), so an older image never builds it as a retail Vendor.
    ///
    /// The owner accepted a Vendor subclass for this one encounter type (2026-11-16), an exception to the encounter
    /// framework's "never subclass WorldObject" rule (IThreadEncounterType, ThreadEncounterTypes.cs): only a Vendor gets
    /// the client's native vendor window.
    ///
    /// CLEAR ONLY (owner rulings 2026-11-19). The NPC exists only after the clear: ThreadPeddlerShop.Present creates it beside
    /// the first seated member who can afford something, at that member's cache spot, and ARMS each member it presents to
    /// (<see cref="Arm"/>). One NPC serves every member of the run; a member it is not beside walks to it. While a member's
    /// shop is open their Thread Cache does not form. A member is DONE, through the run's one latch
    /// (ThreadDungeonRun.TryMarkShopDone, via ThreadPeddlerShop.Release), on whichever comes first:
    ///   (a) out of the use radius continuously for walkAwaySeconds after a Use (closing the client panel sends nothing to
    ///       the server, so this is the server-visible stand-in; coming back in range cancels it);
    ///   (b) Yes on the "Finished trading?" question, sent after a buy only once nothing more can be bought (the buyer's
    ///       <see cref="RowsFor"/> is empty), so several purchases fit in one visit (<see cref="Confirmation_PeddlerShop"/>);
    ///   (c) /spawncache;
    ///   (d) logging out or leaving the run;
    ///   (e) idleSeconds with no Use since presentation, or with no buy and no Use since the last Use or buy.
    /// Every deadline is capped at the run's expiry minus <see cref="ExpiryMarginSeconds"/>. A second Use is NOT done: it
    /// reopens the panel. The peddler leaves when every armed member is done, or the run ends.
    ///
    /// A SIBLING of <see cref="SalvageRollerVendor"/>, not a subclass. It copies its invariants:
    ///  - ethereal and unattackable, set in code as well as in the weenie;
    ///  - empty <see cref="LoadInventory"/>, inert <see cref="RotUniques"/> and <see cref="PrepareResetToHome"/> (a
    ///    placed NPC has no Home, which Vendor.CheckResetToHome would dereference), <see cref="Rotate"/> 0, and an
    ///    <see cref="AddDefaultItem"/> that refuses loudly;
    ///  - every buy and sell cost is 0 and the payout is 0, so the retail purchase arithmetic can never run here;
    ///  - <see cref="ActOnUse"/> does not call base, so Vendor.lastPlayerInfo and CheckClose (a single customer) never run.
    ///
    /// Selling is refused outright (both CanAccept overloads, <see cref="TryValidateSellBasket"/>, and a logged no-op
    /// <see cref="ProcessItemsForPurchase"/>).
    ///
    /// Listings. Built once (<see cref="Bind"/>) and never consumed. The stall is filtered PER VIEWER: a member sees only
    /// the offers priced at or below their own balance, minus any one-per-player offer (PeddlerConfig.OnePerPlayer) they
    /// have already bought, and a viewer without an open shop sees nothing (the three viewer
    /// seams all read <see cref="RowsFor"/>). Each display maps to the purchase record it sells
    /// (<see cref="PeddlerPurchase"/>), and a buy is built ONLY from that map, never from anything the client sent but the
    /// display guid and the amount. Under percent pricing (PeddlerConfig.PricePercent) each member also gets their OWN
    /// displays at <see cref="Arm"/>, valued at their own price from the basis the run snapshot at their shop's open; they
    /// see and may buy only those, and the charge is recomputed server side (ThreadDungeonRun.PeddlerPriceFor).
    ///
    /// Currency. <see cref="Vendor.AlternateCurrency"/> is the Cache Pledge weenie (1007982), never created as an object:
    /// the balance the panel shows is <see cref="AlternateCurrencyBalanceFor"/>, the buyer's real-pile
    /// ThreadDungeonRun.PeddlerBalance. A buy is paid AND delivered at once (owner ruling 2026-11-20, purchases hit the
    /// inventory at purchase time; <see cref="TryBuy"/>): the purchase is built, the buyer's pack is checked against what was
    /// built, the run removes exactly the price in random eligible items from the buyer's held pile under its lock
    /// (ThreadDungeonRun.TryChargePeddlerPurchase), and the built objects go straight into the pack. Nothing is held for the
    /// clear; their Thread Cache forms from the rest of the pile when they are done.
    ///
    /// Threading. A buy arrives as an inbound game action (GameActionBuyItems -> Player.HandleActionBuyItem), which the
    /// world thread runs (WorldManager, before LandblockManager.Tick); Use and the Yes arrive the same way or on the
    /// player's queue; the poll and the departure run on the peddler's own landblock queue (ActionChains on this object).
    /// This object's state sits behind <see cref="gate"/>; the shop latch, the pile and the bought record sit behind the
    /// run's stateLock. Lock order: gate, then the run's lock, never the reverse.
    /// </summary>
    public class ThreadPeddlerVendor : Vendor
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        // ---- player-facing text (owner wording, 2026-11-16 and 2026-11-19) ---------------------------------------

        internal const string PresentedMessage = "The Wandering Peddler finds you as the thread clears. Spend items from your Thread Cache on the wares below, as many times as you like. When you are done, walk away and your Thread Cache forms.";
        internal const string ExclusionMessage = "The Peddler is paid in items taken at random from the ordinary loot of your Thread Cache. Fibers, Patterns, Trade Notes (MMDs), rares and the boss's guaranteed rewards, including its salvage bags, are never taken.";
        internal const string FinishedPromptMessage = "Finished trading with the Wandering Peddler? Choose Yes to form your Thread Cache now, or No to keep shopping.";
        internal const string NothingAffordableMessage = "The Wandering Peddler has nothing you can afford and slips back into the Weave.";
        internal const string DepartureMessage = "The Wandering Peddler packs up and slips back into the Weave.";
        internal const string NonMemberMessage = "Only members of this thread can trade with the Wandering Peddler.";
        internal const string ClosedMessage = "The Wandering Peddler has packed up.";
        internal const string InsufficientMessage = "You have not pledged enough of your Thread Cache for that.";
        internal const string NoSeatMessage = "You hold no share of this thread's loot to trade.";
        internal const string BuysNothingMessage = "The Wandering Peddler buys nothing.";

        /// <summary>A Use or buy by a member the peddler is not (or no longer) trading with. No owner wording was given for it.</summary>
        internal const string NotTradingMessage = "The Wandering Peddler is not trading with you.";

        /// <summary>A buy naming no listing, or an amount below 1. No owner wording was given for it; only a stale or altered client reaches it.</summary>
        internal const string InvalidBuyMessage = "The Wandering Peddler does not have that for sale.";

        /// <summary>A buy that would give the buyer a second of a one-per-player offer (owner wording, 2026-11-20).</summary>
        internal const string LimitReachedMessage = "The Wandering Peddler sells only one of that to each customer.";

        /// <summary>The success line for one of a listing. {0} is what was bought.</summary>
        internal const string PurchaseMessageFormat = "The Wandering Peddler hands you {0}.";

        /// <summary>The success line for N above 1 of one listing. {0} is N, {1} the listing's name.</summary>
        internal const string PurchaseManyMessageFormat = "The Wandering Peddler hands you {0} of {1}.";

        /// <summary>The retail vendor's pack refusals (Vendor.BuyItems_ValidateTransaction), sent the same way.</summary>
        internal const string TooEncumberedMessage = "You are too encumbered to buy that!";
        internal const string NoPackSpaceMessage = "You do not have enough pack space to buy that!";
        internal const string NoContainerSlotsMessage = "You do not have enough container slots to buy that!";

        /// <summary>
        /// The success line for a bought basket: one line per listing bought, the singular wording for an amount of 1
        /// and the plural wording for N above 1. Pure.
        /// </summary>
        internal static IReadOnlyList<string> PurchaseLines(IEnumerable<(string Name, int Amount)> bought)
            => (bought ?? Enumerable.Empty<(string, int)>())
                .Select(b => b.Amount > 1
                    ? string.Format(CultureInfo.InvariantCulture, PurchaseManyMessageFormat, b.Amount, b.Name)
                    : string.Format(CultureInfo.InvariantCulture, PurchaseMessageFormat, b.Name))
                .ToList();

        /// <summary>How often the poll checks every armed member, seconds.</summary>
        internal const double PollIntervalSeconds = 1.0;

        /// <summary>Every shop deadline ends at least this long before the run expires, so a held pile is never forfeited to the TTL.</summary>
        internal const double ExpiryMarginSeconds = 60.0;

        // ---- state ------------------------------------------------------------------------------------------------

        /// <summary>One armed member's timers. Read and written under <see cref="gate"/>.</summary>
        internal sealed class MemberShop
        {
            public double PresentedAt;
            public double? LastUseAt;
            public double? LastBuyAt;
            public double? OutOfRangeSince;
            public bool Done;
        }

        /// <summary>Guards every field below and this peddler's use of DefaultItemsForSale.</summary>
        private readonly object gate = new object();

        private ThreadDungeonRun run;
        private double idleSeconds = PeddlerConfig.DefaultIdleSeconds;
        private double walkAwaySeconds = PeddlerConfig.DefaultWalkAwaySeconds;
        private bool departed;
        private bool polling;
        private double? lastPollAt;

        // Per viewer, the rows one ApproachVendor is serializing (see ApproachVendor): the count and the rows of a single
        // panel message read this one snapshot.
        private readonly Dictionary<uint, List<WorldObject>> panelRows = new Dictionary<uint, List<WorldObject>>();

        /// <summary>Display guid -> the purchase it sells. Exactly the keys of DefaultItemsForSale (pinned by the tests).</summary>
        private readonly Dictionary<ObjectGuid, PeddlerPurchase> listings = new Dictionary<ObjectGuid, PeddlerPurchase>();

        /// <summary>The listings in Bind order: the order every viewer's rows are shown in.</summary>
        private readonly List<ObjectGuid> listingOrder = new List<ObjectGuid>();

        /// <summary>Percent pricing: each member's own displays, in stall order (see <see cref="Arm"/>). Their keys are in <see cref="listings"/> too.</summary>
        private readonly Dictionary<uint, List<ObjectGuid>> memberRows = new Dictionary<uint, List<ObjectGuid>>();

        /// <summary>The member a display was built for (percent pricing); a display absent here is a shared, flat-priced one.</summary>
        private readonly Dictionary<ObjectGuid, uint> listingOwner = new Dictionary<ObjectGuid, uint>();

        /// <summary>
        /// Percent pricing (set by PeddlerEncounterType.CreateNpcAt when the plan has it): builds one member's own displays at
        /// their own price, called by <see cref="Arm"/> after their shop opened. Null: every member sees the shared rows.
        /// </summary>
        internal Func<uint, IReadOnlyList<(WorldObject Display, PeddlerPurchase Purchase)>> MemberListings;

        /// <summary>
        /// Test seam: runs inside <see cref="Arm"/>'s install block, under <see cref="gate"/>, after the IsDestroyed check and
        /// before the member's displays are installed: the window in which a raw Destroy (which takes no gate) can race it.
        /// Null in production.
        /// </summary>
        internal Action BeforeInstallForTest;

        /// <summary>Armed members, by guid.</summary>
        private readonly Dictionary<uint, MemberShop> shops = new Dictionary<uint, MemberShop>();

        /// <summary>Test seam: the clock, unix seconds. Per instance, so no test leaks it.</summary>
        internal Func<double> Clock = () => Time.GetUnixTime();

        /// <summary>Test seam: "is this player a member standing in the run". Production is ThreadRunPresence.IsMemberInside.</summary>
        internal Func<ThreadDungeonRun, Player, bool> InsideCheck = ThreadRunPresence.IsMemberInside;

        /// <summary>Test seam: is the player within use radius of the peddler. Production is Player.IsWithinUseRadiusOf.</summary>
        internal Func<Player, bool> RangeCheck;

        /// <summary>Test seam: an online player by guid. Production is PlayerManager.GetOnlinePlayer.</summary>
        internal Func<uint, Player> PlayerLookup = guid => PlayerManager.GetOnlinePlayer(guid);

        /// <summary>Test seam: sends the "Finished trading?" Yes_No; false when an unrelated one is pending. Production is <see cref="DefaultFinishedAsk"/>.</summary>
        internal Func<Player, string, bool> FinishedAsk;

        /// <summary>Test seam: replaces the ConfirmationInProgress error sent when the question could not be asked.</summary>
        internal Action<Player> AskFailed;

        /// <summary>Test seam: sees every line the peddler tells a player.</summary>
        internal Action<Player, string> Told;

        /// <summary>Test seam: the panel refresh (ApproachVendor). Null uses the real one when the player has a session.</summary>
        internal Action<Player, VendorType, uint> Approach;

        /// <summary>Test seam: builds one purchase's objects, unplaced. Production is ThreadPeddlerPayment.BuildPurchase.</summary>
        internal Func<ThreadDungeonRun, PeddlerPurchase, Random, List<WorldObject>> Builder = ThreadPeddlerPayment.BuildPurchase;

        /// <summary>Test seam: the buyer's pack. Production is <see cref="PeddlerReceiver.For"/>.</summary>
        internal Func<Player, PeddlerReceiver> ReceiverOf = PeddlerReceiver.For;

        /// <summary>Test seam: puts a paid object the pack refused into the buyer's pile. Production is ThreadDungeonRun.AddToHeldPile.</summary>
        internal Func<ThreadDungeonRun, uint, HeldPileItem, bool> PileAdd = (r, guid, held) => r.AddToHeldPile(guid, held);

        /// <summary>Test seam: the Random a buy builds and draws payment with. Null is Random.Shared.</summary>
        internal Random BuyRng;

        /// <summary>
        /// A new biota be created taking all of its values from weenie.
        /// </summary>
        public ThreadPeddlerVendor(Weenie weenie, ObjectGuid guid) : base(weenie, guid)
        {
            FinishedAsk = DefaultFinishedAsk;
            RangeCheck = p => p.IsWithinUseRadiusOf(this);
            SetEphemeralValues();
        }

        /// <summary>
        /// Restore a WorldObject from the database. Implemented so a stray peddler biota never comes back as a plain
        /// Creature or retail Vendor.
        /// </summary>
        public ThreadPeddlerVendor(Biota biota) : base(biota)
        {
            FinishedAsk = DefaultFinishedAsk;
            RangeCheck = p => p.IsWithinUseRadiusOf(this);
            SetEphemeralValues();
        }

        /// <summary>Never a body-blocker, never an aggro target. Set here AND in the weenie.</summary>
        private void SetEphemeralValues()
        {
            Ethereal = true;
            Attackable = false;
        }

        /// <summary>The run this peddler trades for; null before <see cref="Bind"/> (an unplaced copy, an admin @create).</summary>
        public ThreadDungeonRun Run { get { lock (gate) return run; } }

        /// <summary>Has the peddler left (or been told the run ended)? It trades nothing after that.</summary>
        internal bool Departed { get { lock (gate) return departed; } }

        /// <summary>A snapshot of the listings: display guid -> purchase record.</summary>
        internal IReadOnlyDictionary<ObjectGuid, PeddlerPurchase> Listings { get { lock (gate) return new Dictionary<ObjectGuid, PeddlerPurchase>(listings); } }

        /// <summary>A poll older than this means the poll has stopped (ThreadPeddlerShop.SweepLiveness).</summary>
        internal const double PollStaleSeconds = 15.0;

        /// <summary>
        /// Can this NPC still release anyone? Not destroyed, not departed, and its poll (once started) ran within
        /// <see cref="PollStaleSeconds"/> of <paramref name="now"/>.
        /// </summary>
        internal bool IsAlive(double now)
        {
            if (IsDestroyed)
                return false;

            lock (gate)
                return !departed && (lastPollAt == null || now - lastPollAt.Value < PollStaleSeconds);
        }

        /// <summary>Test seam: when the poll last ran (null before StartWatch).</summary>
        internal double? LastPollAt
        {
            get { lock (gate) return lastPollAt; }
            set { lock (gate) lastPollAt = value; }
        }

        /// <summary>The configured idle and walk-away seconds.</summary>
        internal (double Idle, double WalkAway) Timings { get { lock (gate) return (idleSeconds, walkAwaySeconds); } }

        /// <summary>A copy of one armed member's timers, or null when not armed.</summary>
        internal MemberShop ShopOf(uint memberGuid)
        {
            lock (gate)
            {
                if (!shops.TryGetValue(memberGuid, out var s))
                    return null;

                return new MemberShop { PresentedAt = s.PresentedAt, LastUseAt = s.LastUseAt, LastBuyAt = s.LastBuyAt, OutOfRangeSince = s.OutOfRangeSince, Done = s.Done };
            }
        }

        /// <summary>The armed members' guids.</summary>
        internal IReadOnlyList<uint> ArmedMembers { get { lock (gate) return shops.Keys.ToList(); } }

        #region SalvageRollerVendor invariants

        /// <summary>The listings come from <see cref="Bind"/>, never from a create list.</summary>
        protected override void LoadInventory()
        {
        }

        /// <summary>UniqueItemsForSale is never populated here, so the retail rot-and-destroy path has nothing to do.</summary>
        protected override void RotUniques()
        {
        }

        /// <summary>Inert: the peddler has no Home, which Vendor.CheckResetToHome would dereference.</summary>
        protected override void PrepareResetToHome()
        {
        }

        /// <summary>Inert: never turns toward one customer.</summary>
        public override float Rotate(WorldObject target) => 0f;

        public override uint GetSellCost(WorldObject item) => 0;

        public override uint GetSellCost(Weenie item) => 0;

        public override int GetBuyCost(WorldObject item) => 0;

        public override int GetBuyCost(Weenie item) => 0;

        /// <summary>Pays nothing: the base pays at least 1 pyreal per item (Vendor.GetBuyCost floors at 1).</summary>
        public override int CalculatePayoutCoinAmount(Dictionary<uint, WorldObject> items) => 0;

        /// <summary>Refuses everything: listings enter only through <see cref="Bind"/>. Loud, because a caller reaching this would put something into a dictionary WorldObject.Destroy walks.</summary>
        public override void AddDefaultItem(WorldObject item)
        {
            log.Error($"[THREAD PEDDLER] {Name}: refused to add 0x{item?.Guid.Full ?? 0:X8} ({item?.Name}) through AddDefaultItem. Listings are set only by Bind.");
        }

        #endregion

        #region Sell (refused)

        /// <summary>Refuses every item, so the sell path never detaches anything for this vendor.</summary>
        public override bool CanAccept(WorldObject wo, Player player, out string reason)
        {
            reason = BuysNothingMessage;
            return false;
        }

        /// <summary>Refuses every item; does not defer to base (whose default is the permissive single-item overload).</summary>
        public override bool CanAccept(WorldObject wo, Player player, VaultAccess resolvedAccess, out string reason)
        {
            reason = BuysNothingMessage;
            return false;
        }

        /// <summary>Refuses the whole basket before anything is detached.</summary>
        public override bool TryValidateSellBasket(Player player, IReadOnlyList<WorldObject> requested, IReadOnlyList<WorldObject> accepted, out string reason)
        {
            reason = BuysNothingMessage;
            return false;
        }

        /// <summary>
        /// Logged no-op: the base would keep the items in UniqueItemsForSale, which the inert RotUniques never clears.
        /// Unreachable while the refusals above hold; loud, because reaching it means a caller skipped them.
        /// </summary>
        public override void ProcessItemsForPurchase(Player player, Dictionary<uint, WorldObject> items)
        {
            log.Error($"[THREAD PEDDLER] {Name}: refused ProcessItemsForPurchase of {items?.Count ?? 0} item(s) from {player?.Name}. The peddler buys nothing.");
        }

        #endregion

        #region Listings

        /// <summary>
        /// Binds the peddler to its run and installs its listings, once, before EnterWorld. Each display must be a
        /// disposable object (never saved to the shard, not wielded, not inside another container) that is not already a
        /// listing; it is then parented to the peddler, given unlimited supply (VendorShopCreateListStackSize -1) and put in
        /// DefaultItemsForSale, mapped to its purchase record. A refused display is left exactly as it is (and logged), so a
        /// builder bug can never hand a real item to the destroy path. Returns how many listings were installed; 0, changing
        /// nothing, when already bound.
        /// </summary>
        internal int Bind(ThreadDungeonRun boundRun, double idle, double walkAway, IEnumerable<(WorldObject Display, PeddlerPurchase Purchase)> offers)
        {
            if (boundRun == null)
                return 0;

            lock (gate)
            {
                if (run != null)
                {
                    log.Error($"[THREAD PEDDLER] {Name}: already bound to {run}; a second Bind is ignored.");
                    return 0;
                }

                run = boundRun;
                idleSeconds = idle > 0 && double.IsFinite(idle) ? idle : PeddlerConfig.DefaultIdleSeconds;
                walkAwaySeconds = walkAway > 0 && double.IsFinite(walkAway) ? walkAway : PeddlerConfig.DefaultWalkAwaySeconds;

                foreach (var (display, purchase) in offers ?? Enumerable.Empty<(WorldObject, PeddlerPurchase)>())
                {
                    if (display == null || purchase == null)
                        continue;

                    if (TryInstallLocked(display, purchase))
                        listingOrder.Add(display.Guid);
                }

                return listings.Count;
            }
        }

        /// <summary>
        /// Installs one display under <see cref="gate"/>: it must not already be a listing and must be disposable (else it is
        /// left exactly as it is, logged). It is parented to the peddler, as every vendor's stock is (the vendor root-owner
        /// guards in Player_Inventory key off it, which keeps a display out of a player's pack), given unlimited supply, and
        /// put in DefaultItemsForSale mapped to its purchase.
        /// </summary>
        private bool TryInstallLocked(WorldObject display, PeddlerPurchase purchase)
        {
            if (DefaultItemsForSale.ContainsKey(display.Guid))
            {
                log.Error($"[THREAD PEDDLER] {Name}: display 0x{display.Guid.Full:X8} is already a listing; not listed twice.");
                return false;
            }

            if (!IsDisposableDisplay(display, out var whyNot))
            {
                log.Error($"[THREAD PEDDLER] {Name}: display 0x{display.Guid.Full:X8} ({display.Name}, wcid {display.WeenieClassId}) {whyNot}; it is not a disposable display object and is not listed.");
                return false;
            }

            display.ContainerId = Guid.Full;
            display.VendorShopCreateListStackSize = -1;

            DefaultItemsForSale[display.Guid] = display;
            listings[display.Guid] = purchase;
            return true;
        }

        /// <summary>The rows a member is offered, in stall order, under <see cref="gate"/>: their own displays under percent pricing, else the shared ones.</summary>
        private IEnumerable<ObjectGuid> RowKeysLocked(uint memberGuid, bool percent)
        {
            if (memberRows.TryGetValue(memberGuid, out var own))
                return own;

            // Percent pricing without own rows (Arm refuses to arm that): nothing, never the flat shared rows.
            return percent ? Enumerable.Empty<ObjectGuid>() : listingOrder;
        }

        private bool IsDisposableDisplay(WorldObject display, out string whyNot)
        {
            if (display.BiotaOriginatedFromOrHasBeenSavedToDatabase())
            {
                whyNot = "has been saved to the shard";
                return false;
            }

            if (display.WielderId != null)
            {
                whyNot = $"is wielded by 0x{display.WielderId:X8}";
                return false;
            }

            if (display.ContainerId != null && display.ContainerId != Guid.Full)
            {
                whyNot = $"is inside container 0x{display.ContainerId:X8}";
                return false;
            }

            whyNot = null;
            return true;
        }

        /// <summary>
        /// The rows <paramref name="viewer"/> is shown, in Bind order (under percent pricing, their OWN displays, each at their
        /// own price; see <see cref="Arm"/>): every listing priced at or below their balance, except
        /// a one-per-player listing (the run plan's PeddlerConfig.OnePerPlayer) whose entry id they have already bought,
        /// and only while their shop is open. Nothing for anyone else. What they bought is read through the run's own
        /// accessor (lock order: gate, then the run's lock). A LIVE read of state other threads change (the pile, a
        /// charge, the shop latch), so two calls can differ; one panel message therefore reads one snapshot of it
        /// (<see cref="ApproachVendor"/>, <see cref="PanelRowsFor"/>).
        /// </summary>
        internal List<WorldObject> RowsFor(Player viewer)
        {
            var rows = new List<WorldObject>();

            if (viewer == null)
                return rows;

            var guid = viewer.Guid.Full;

            lock (gate)
            {
                if (run == null || departed || !shops.TryGetValue(guid, out var shop) || shop.Done || !run.IsPeddlerShopOpen(guid))
                    return rows;

                var balance = run.PeddlerBalance(guid);
                var held = HeldCappedEntries(run, guid);
                var percent = run.PeddlerPlan?.HasPercentPricing == true;

                foreach (var key in RowKeysLocked(guid, percent))
                {
                    if (listings.TryGetValue(key, out var purchase) && purchase.Price <= balance && !held.Contains(purchase.EntryId)
                        && DefaultItemsForSale.TryGetValue(key, out var display))
                        rows.Add(display);
                }
            }

            return rows;
        }

        /// <summary>
        /// The one-per-player entry ids (the run plan's PeddlerConfig.OnePerPlayer) that <paramref name="memberGuid"/> has
        /// already bought. Empty without a plan or a cap. Takes the run's lock through its accessors.
        /// </summary>
        private static HashSet<string> HeldCappedEntries(ThreadDungeonRun bound, uint memberGuid)
        {
            var capped = bound.PeddlerPlan?.Config.OnePerPlayer;

            if (capped == null || capped.Count == 0)
                return new HashSet<string>(StringComparer.Ordinal);

            return new HashSet<string>(bound.PeddlerBought(memberGuid).Where(capped.Contains), StringComparer.Ordinal);
        }

        /// <summary>
        /// Should a buy just made for <paramref name="player"/> ask "Finished trading?": only while their shop is still
        /// open (bound, not departed, armed and not done here, open on the run) AND nothing is left for them to buy. The open
        /// check matters because <see cref="RowsFor"/> is also empty for a closed shop, so a buy racing the poll, a release or
        /// the run's end would otherwise send a stray prompt for a shop that has gone. Lock order gate, then the run's lock;
        /// RowsFor re-enters gate, which is safe because Monitor is re-entrant on the same thread.
        /// </summary>
        internal bool ShouldAskFinished(Player player)
        {
            if (player == null)
                return false;

            var guid = player.Guid.Full;

            lock (gate)
            {
                if (run == null || departed || !shops.TryGetValue(guid, out var shop) || shop.Done || !run.IsPeddlerShopOpen(guid))
                    return false;

                return RowsFor(player).Count == 0;
            }
        }

        /// <summary>
        /// The rows for one panel message: the snapshot <see cref="ApproachVendor"/> took for this viewer, or a live
        /// <see cref="RowsFor"/> outside one.
        /// </summary>
        internal List<WorldObject> PanelRowsFor(Player viewer)
        {
            if (viewer != null)
            {
                lock (gate)
                {
                    if (panelRows.TryGetValue(viewer.Guid.Full, out var snap))
                        return snap;
                }
            }

            return RowsFor(viewer);
        }

        /// <summary>
        /// Takes one snapshot of the viewer's rows for the duration of <paramref name="body"/>, so GameEventApproachVendor's
        /// count field (<see cref="GetItemsForSaleCount"/>) and its rows (<see cref="ForEachItemFor"/>) are the same list
        /// even if the balance changes on another thread between the two reads.
        /// </summary>
        internal void WithPanelSnapshot(Player viewer, Action body)
        {
            if (viewer == null)
            {
                body();
                return;
            }

            var guid = viewer.Guid.Full;
            var rows = RowsFor(viewer);

            lock (gate)
                panelRows[guid] = rows;

            try
            {
                body();
            }
            finally
            {
                lock (gate)
                    panelRows.Remove(guid);
            }
        }

        /// <summary>Every panel message (Use, the buy refresh, and the refused-buy re-send in Player.HandleActionBuyItem) reads one row snapshot.</summary>
        public override void ApproachVendor(Player player, VendorType action = VendorType.Undef, uint altCurrencySpent = 0)
            => WithPanelSnapshot(player, () => base.ApproachVendor(player, action, altCurrencySpent));

        /// <summary>The viewer's affordable rows, from the panel snapshot when one is being written.</summary>
        public override void ForEachItemFor(Player viewer, Action<WorldObject> action)
        {
            foreach (var display in PanelRowsFor(viewer))
                action(display);
        }

        /// <summary>The same snapshot as <see cref="ForEachItemFor"/>, which GameEventApproachVendor relies on for its count field.</summary>
        public override int GetItemsForSaleCount(Player viewer) => PanelRowsFor(viewer).Count;

        /// <summary>Only a row the viewer is shown resolves (appraisal); the buy itself resolves from the listing map.</summary>
        public override bool TryGetItemForSale(Player viewer, ObjectGuid itemGuid, out WorldObject itemForSale)
        {
            itemForSale = PanelRowsFor(viewer).FirstOrDefault(d => d.Guid == itemGuid);
            return itemForSale != null;
        }

        /// <summary>Fails closed: with no viewer there is nobody whose balance could filter the stall.</summary>
        public override void forEachItem(Action<WorldObject> action)
        {
        }

        /// <summary>Fails closed, equal to what <see cref="forEachItem"/> emits.</summary>
        public override int ItemsForSaleCount => 0;

        #endregion

        #region Currency

        /// <summary>
        /// The panel's balance: for the peddler's own currency (the Cache Pledge), the viewer's real-pile
        /// ThreadDungeonRun.PeddlerBalance while their shop is open; 0 for any other wcid, for a viewer without an open shop,
        /// and before the peddler is bound. Never an inventory read: the Cache Pledge is never created as an object.
        /// </summary>
        public override long AlternateCurrencyBalanceFor(Player viewer, uint wcid)
        {
            var bound = Run;

            if (viewer == null || bound == null || AlternateCurrency == null || wcid != AlternateCurrency.Value)
                return 0;

            if (!bound.IsPeddlerShopOpen(viewer.Guid.Full))
                return 0;

            return Math.Max(0, bound.PeddlerBalance(viewer.Guid.Full));
        }

        #endregion

        #region Arm and Use

        /// <summary>
        /// Arms one member (ThreadPeddlerShop.Present, after the run opened their shop): their idle timer starts now. Under
        /// percent pricing their own displays are built (<see cref="MemberListings"/>) and installed here, each at their own
        /// price, and arming fails when none builds. False, changing nothing, when the peddler is unbound or has left; true
        /// when armed now or already armed.
        /// </summary>
        internal bool Arm(uint memberGuid)
        {
            ThreadDungeonRun bound;

            lock (gate)
            {
                bound = run;

                if (run == null || departed || IsDestroyed)
                    return false;

                if (shops.ContainsKey(memberGuid))
                    return true;
            }

            // Percent pricing: the member's own displays, built outside the gate (object factories), priced from the basis
            // the run snapshot when their shop opened.
            List<(WorldObject Display, PeddlerPurchase Purchase)> own = null;

            if (bound.PeddlerPlan?.HasPercentPricing == true)
            {
                own = MemberListings?.Invoke(memberGuid)?.Where(l => l.Display != null && l.Purchase != null).ToList() ?? new List<(WorldObject, PeddlerPurchase)>();

                if (own.Count == 0)
                {
                    log.Warn($"[DYNDUNGEON] {bound} peddler: no own listings built for 0x{memberGuid:X8} under percent pricing; not armed");
                    return false;
                }
            }

            var unused = new List<WorldObject>();
            bool armed;

            lock (gate)
            {
                if (run == null || departed || IsDestroyed)
                    armed = false;
                else if (shops.ContainsKey(memberGuid))
                    armed = true;
                else
                {
                    BeforeInstallForTest?.Invoke();

                    var rows = new List<ObjectGuid>();

                    foreach (var (display, purchase) in own ?? new List<(WorldObject, PeddlerPurchase)>())
                    {
                        if (TryInstallLocked(display, purchase))
                        {
                            rows.Add(display.Guid);
                            listingOwner[display.Guid] = memberGuid;
                        }
                    }

                    if (own != null && rows.Count == 0)
                        armed = false;
                    else
                    {
                        if (own != null)
                            memberRows[memberGuid] = rows;

                        shops[memberGuid] = new MemberShop { PresentedAt = Clock() };
                        armed = true;
                    }
                }
            }

            // Review of 05daac285 (F1): WorldObject.Destroy takes no gate. When it raced the install above (it marks the
            // peddler destroyed, then destroys a snapshot of DefaultItemsForSale that may predate the install), the displays
            // just installed would outlive it. Destroy them here (idempotent) and report not armed.
            if (armed && IsDestroyed)
            {
                foreach (var (display, _) in own ?? new List<(WorldObject, PeddlerPurchase)>())
                {
                    try { if (!display.IsDestroyed) display.Destroy(); } catch (Exception ex) { log.Error($"[DYNDUNGEON] {bound} peddler: destroying a display installed after the peddler was destroyed threw", ex); }
                }

                return false;
            }

            // Built but never installed (the peddler left, or another call armed them first): nobody else holds these.
            foreach (var (display, _) in own ?? new List<(WorldObject, PeddlerPurchase)>())
                unused.Add(display);

            foreach (var display in unused)
            {
                // ContainerId is set only by an install, so a display still without one was never listed.
                try { if (!display.IsDestroyed && display.ContainerId == null) display.Destroy(); } catch { }
            }

            return armed;
        }

        internal enum UseOutcome
        {
            /// <summary>The peddler has left, or was never bound.</summary>
            Closed,

            /// <summary>The user is not a member standing in the run.</summary>
            NotMember,

            /// <summary>The user is a member, but the peddler is not (or no longer) trading with them.</summary>
            NotTrading,

            /// <summary>The user's shop is open: the panel opens (a repeat Use reopens it).</summary>
            Open,
        }

        /// <summary>
        /// The Player-free core of a Use. Only an armed member whose shop is open is served; the Use is recorded (it restarts
        /// the idle clock and clears any walk-away countdown). A repeat Use is served the same way: never a done signal.
        /// </summary>
        internal UseOutcome TryUse(uint userGuid, bool userInside)
        {
            lock (gate)
            {
                if (run == null || departed)
                    return UseOutcome.Closed;

                if (!userInside)
                    return UseOutcome.NotMember;

                if (!shops.TryGetValue(userGuid, out var shop) || shop.Done || !run.IsPeddlerShopOpen(userGuid))
                    return UseOutcome.NotTrading;

                shop.LastUseAt = Clock();
                shop.OutOfRangeSince = null;
                return UseOutcome.Open;
            }
        }

        /// <summary>
        /// Use: the busy check, then <see cref="TryUse"/>. Deliberately does NOT call base: Vendor.ActOnUse keeps one
        /// lastPlayerInfo and runs CheckClose for a single customer, and rotates toward the user. The peddler's type does not
        /// implement IThreadEncounterUsable, so this is the one place a Use is handled.
        /// </summary>
        public override void ActOnUse(WorldObject wo)
        {
            if (!(wo is Player player))
                return;

            // Vendor's constructor sets OpenForBusiness from ValidateVendorRequirements: a peddler weenie missing a vendor
            // row (or the Cache Pledge weenie) is closed, and opens nothing.
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

            var bound = Run;
            var inside = bound != null && InsideCheck(bound, player);

            switch (TryUse(player.Guid.Full, inside))
            {
                case UseOutcome.Closed:
                    Tell(player, ClosedMessage);
                    return;

                case UseOutcome.NotMember:
                    Tell(player, NonMemberMessage);
                    return;

                case UseOutcome.NotTrading:
                    Tell(player, NotTradingMessage);
                    return;
            }

            ShowPanel(player, VendorType.Open, 0);
        }

        private void ShowPanel(Player player, VendorType type, uint spent)
        {
            if (Approach != null)
            {
                Approach(player, type, spent);
                return;
            }

            if (player.Session != null)
                ApproachVendor(player, type, spent);
        }

        #endregion

        #region Buy (paid and delivered at once)

        internal enum BuyOutcome
        {
            Bought,
            Closed,
            NotMember,
            NotTrading,
            Invalid,
            Insufficient,
            NoSeat,
            LimitReached,

            /// <summary>A record in the basket built nothing now (the Knots gate closed, a Pattern's pinnacle left the pool, no weenie).</summary>
            Unavailable,

            TooEncumbered,
            NoPackSpace,
            NoContainerSlots,
        }

        /// <summary>What <see cref="TryBuy"/> resolved and did.</summary>
        internal sealed class BuyResult
        {
            public BuyOutcome Outcome;

            /// <summary>The records charged (or that would have been): built from the listings alone.</summary>
            public List<PeddlerPurchase> Records = new List<PeddlerPurchase>();

            public long Total;

            /// <summary>What was bought, for the chat line: "Diamond Scarab x5", "3 x Promissory Note x5", joined with ", ".</summary>
            public string Label;

            /// <summary>Each listing in the basket with its amount, in basket order: what the purchase lines name.</summary>
            public List<(string Name, int Amount)> Bought = new List<(string Name, int Amount)>();

            /// <summary>The objects that went into the buyer's pack.</summary>
            public List<WorldObject> Given = new List<WorldObject>();

            /// <summary>The objects the pack refused after payment, put in the buyer's held pile instead (they reach their Thread Cache).</summary>
            public List<WorldObject> ToPile = new List<WorldObject>();

            /// <summary>How many payment items the charge removed from the pile (always Total on a buy).</summary>
            public int PaymentItems;
        }

        /// <summary>
        /// The buyer's pack, as a buy sees it: free slots and burden read once, and the hand-over. Production wraps a Player
        /// (<see cref="For"/>); tests build one directly.
        /// </summary>
        internal sealed class PeddlerReceiver
        {
            public int FreeInventorySlots;
            public int FreeContainerSlots;
            public long AvailableBurden;

            /// <summary>Puts one object in the pack; false when it did not go in.</summary>
            public Func<WorldObject, bool> Give;

            /// <summary>
            /// The player's pack (ItemsToReceive's three reads), handing over through TryCreateInInventoryWithNetworking (the
            /// PersonalVendor withdrawal pattern). Null for no player. An object is never given without a session.
            /// </summary>
            public static PeddlerReceiver For(Player player)
            {
                if (player == null)
                    return null;

                return new PeddlerReceiver
                {
                    FreeInventorySlots = player.GetFreeInventorySlots(),
                    FreeContainerSlots = player.GetFreeContainerSlots(),
                    AvailableBurden = player.GetAvailableBurden(),
                    Give = GiveTo(player),
                };
            }

            /// <summary>The production hand-over for <paramref name="player"/>: false, placing nothing, without a session.</summary>
            internal static Func<WorldObject, bool> GiveTo(Player player)
                => item => player?.Session != null && player.TryCreateInInventoryWithNetworking(item);
        }

        /// <summary>The chat line's item text for one listing bought <paramref name="amount"/> times. Pure.</summary>
        internal static string AmountLabel(string name, int amount)
            => amount > 1 ? $"{amount.ToString(CultureInfo.InvariantCulture)} x {name}" : name;

        /// <summary>
        /// The pack refusal for <paramref name="objects"/> against <paramref name="receiver"/>, in the retail vendor's order
        /// (burden, then pack slots, then container slots); null when they fit. Pure.
        /// </summary>
        internal static BuyOutcome? PackRefusal(IReadOnlyList<WorldObject> objects, PeddlerReceiver receiver)
        {
            var (inventory, container, burden) = ThreadPeddlerPayment.PackNeeds(objects);

            if (burden > receiver.AvailableBurden)
                return BuyOutcome.TooEncumbered;

            if (inventory > receiver.FreeInventorySlots)
                return BuyOutcome.NoPackSpace;

            if (container > receiver.FreeContainerSlots)
                return BuyOutcome.NoContainerSlots;

            return null;
        }

        /// <summary><see cref="TryBuy(uint, bool, IReadOnlyList{ItemProfile}, Func{PeddlerReceiver})"/> with the buyer's pack found by guid (<see cref="PlayerLookup"/>, <see cref="ReceiverOf"/>).</summary>
        internal BuyResult TryBuy(uint buyerGuid, bool buyerInside, IReadOnlyList<ItemProfile> profiles)
            => TryBuy(buyerGuid, buyerInside, profiles, null);

        /// <summary>
        /// The Player-free core of a buy, in order:
        ///  1. Validate: the peddler is here; the buyer is a member inside; their shop is open; every profile names a listing
        ///     and a valid amount (ItemProfile.IsValidAmount); no one-per-player entry would be held more than once (bought
        ///     plus basket, by entry id: the cheap pre-check, so an amount of 2 or more refuses as LimitReached whatever the
        ///     balance); the basket total fits the real-pile balance (checked before the records are expanded, so a huge
        ///     amount costs nothing). Then amount N of a listing becomes N copies of THAT listing's record.
        ///  2. Build every record (<see cref="Builder"/>). A record that builds nothing refuses the WHOLE basket
        ///     (<see cref="BuyOutcome.Unavailable"/>): what was built is destroyed and nothing is charged.
        ///  3. Check the buyer's pack against the built objects (<see cref="PackRefusal"/>); a refusal destroys what was
        ///     built and charges nothing.
        ///  4. Charge: ThreadDungeonRun.TryChargePeddlerPurchase re-checks the shop, the seat, the cap and the balance under
        ///     the run's lock (the authoritative check) and removes exactly the total in random eligible pile items. A
        ///     refusal destroys what was built.
        ///  5. Hand each object over (<see cref="PeddlerReceiver.Give"/>). One the pack refuses now is NEVER destroyed: it
        ///     goes into the buyer's held pile as a peddler purchase, so it reaches their Thread Cache (logged Warn).
        ///  6. Destroy the payment items, outside the run's lock.
        /// Nothing the client sent is read but each profile's display guid and amount: entry id, price and Pattern come
        /// from the listing. <paramref name="receiver"/> is asked for only at step 3; null finds the buyer by guid.
        /// </summary>
        internal BuyResult TryBuy(uint buyerGuid, bool buyerInside, IReadOnlyList<ItemProfile> profiles, Func<PeddlerReceiver> receiver)
        {
            var result = new BuyResult();
            ThreadDungeonRun bound;
            var resolved = new List<(PeddlerPurchase Purchase, string Name, int Amount)>();

            lock (gate)
            {
                bound = run;

                if (bound == null || departed)
                {
                    result.Outcome = BuyOutcome.Closed;
                    return result;
                }

                if (!buyerInside)
                {
                    result.Outcome = BuyOutcome.NotMember;
                    return result;
                }

                if (!shops.TryGetValue(buyerGuid, out var shop) || shop.Done || !bound.IsPeddlerShopOpen(buyerGuid))
                {
                    result.Outcome = BuyOutcome.NotTrading;
                    return result;
                }

                if (profiles == null || profiles.Count == 0)
                {
                    result.Outcome = BuyOutcome.Invalid;
                    return result;
                }

                foreach (var profile in profiles)
                {
                    if (profile == null || !profile.IsValidAmount
                        || !listings.TryGetValue(new ObjectGuid(profile.ObjectGuid), out var purchase)
                        || !DefaultItemsForSale.TryGetValue(new ObjectGuid(profile.ObjectGuid), out var display))
                    {
                        result.Outcome = BuyOutcome.Invalid;
                        return result;
                    }

                    // A row is bought only by the member it is shown to: a member's own display (percent pricing) by them
                    // alone, and a shared display only by a member who has no own rows.
                    var key = new ObjectGuid(profile.ObjectGuid);
                    var ownedBy = listingOwner.TryGetValue(key, out var owner) ? owner : (uint?)null;

                    if (ownedBy != null ? ownedBy.Value != buyerGuid : memberRows.ContainsKey(buyerGuid))
                    {
                        result.Outcome = BuyOutcome.Invalid;
                        return result;
                    }

                    resolved.Add((purchase, display.Name, profile.Amount));
                }
            }

            // The charge is computed server side from the buyer's own basis snapshot (ThreadDungeonRun.PeddlerPriceFor), never
            // from the client and never from another member's display; it is the same number the buyer's display shows. Flat
            // pricing keeps the listing's price.
            if (bound.PeddlerPlan?.HasPercentPricing == true)
            {
                for (var i = 0; i < resolved.Count; i++)
                {
                    var (purchase, name, amount) = resolved[i];
                    var charge = bound.PeddlerPriceFor(buyerGuid, purchase.EntryId) ?? purchase.Price;
                    resolved[i] = (new PeddlerPurchase(purchase.EntryId, charge, purchase.Pattern), name, amount);
                }
            }

            // Long arithmetic: amount is at most int.MaxValue and a price a few hundred, so the sum cannot overflow.
            result.Total = resolved.Sum(r => (long)r.Purchase.Price * r.Amount);
            result.Label = string.Join(", ", resolved.Select(r => AmountLabel(r.Name, r.Amount)));
            result.Bought = resolved.Select(r => (r.Name, r.Amount)).ToList();

            if (ExceedsOnePerPlayer(bound, buyerGuid, resolved))
            {
                result.Outcome = BuyOutcome.LimitReached;
                return result;
            }

            // Refused before expanding: every record costs at least 1, so a basket that fits has at most balance records.
            if (result.Total > bound.PeddlerBalance(buyerGuid))
            {
                result.Outcome = BuyOutcome.Insufficient;
                return result;
            }

            foreach (var (purchase, _, amount) in resolved)
            {
                for (var i = 0; i < amount; i++)
                    result.Records.Add(new PeddlerPurchase(purchase.EntryId, purchase.Price, purchase.Pattern));
            }

            var rng = BuyRng ?? Random.Shared;
            var built = new List<WorldObject>();

            // 2. Build every record; one that builds nothing refuses the whole basket.
            try
            {
                foreach (var record in result.Records)
                {
                    var objects = Builder?.Invoke(bound, record, rng);

                    if (objects == null || objects.Count(o => o != null) == 0)
                    {
                        log.Warn($"[DYNDUNGEON] {bound} peddler: 0x{buyerGuid:X8}'s purchase {record.EntryId} built nothing now; the basket is refused, nothing charged");
                        DestroyAll(bound, built);
                        result.Outcome = BuyOutcome.Unavailable;
                        return result;
                    }

                    built.AddRange(objects.Where(o => o != null));
                }
            }
            catch (Exception ex)
            {
                log.Error($"[DYNDUNGEON] {bound} peddler: building 0x{buyerGuid:X8}'s purchase threw; the basket is refused, nothing charged", ex);
                DestroyAll(bound, built);
                result.Outcome = BuyOutcome.Unavailable;
                return result;
            }

            // 3. The pack, against what was actually built.
            PeddlerReceiver pack;

            try
            {
                pack = (receiver ?? (() => ReceiverOf?.Invoke(PlayerLookup(buyerGuid))))();
            }
            catch (Exception ex)
            {
                log.Error($"[DYNDUNGEON] {bound} peddler: reading 0x{buyerGuid:X8}'s pack threw; the basket is refused, nothing charged", ex);
                pack = null;
            }

            if (pack == null || pack.Give == null)
            {
                DestroyAll(bound, built);
                result.Outcome = BuyOutcome.NotTrading;
                return result;
            }

            var refusal = PackRefusal(built, pack);

            if (refusal != null)
            {
                DestroyAll(bound, built);
                result.Outcome = refusal.Value;
                return result;
            }

            // 4. The charge, authoritative, under the run's lock.
            var charged = bound.TryChargePeddlerPurchase(buyerGuid, result.Records, (eligible, n) => ThreadPeddlerPayment.PickPaymentItems(eligible, n, rng), out var removed);

            if (!charged.IsCharged)
            {
                DestroyAll(bound, built);

                result.Outcome = charged.Outcome switch
                {
                    PeddlerChargeOutcome.Insufficient => BuyOutcome.Insufficient,
                    PeddlerChargeOutcome.NoSeat => BuyOutcome.NoSeat,
                    PeddlerChargeOutcome.Invalid => BuyOutcome.Invalid,
                    PeddlerChargeOutcome.LimitReached => BuyOutcome.LimitReached,
                    _ => BuyOutcome.NotTrading, // ShopNotOpen: the member is done, or the run left Cleared
                };

                return result;
            }

            result.Outcome = BuyOutcome.Bought;
            result.PaymentItems = removed.Count;

            // 5. Paid: every built object ends in the pack or, refused there, in the buyer's pile. Never destroyed here.
            // A throw for one object (anywhere in its hand-over) is logged and rescued into the pile, and never stops the
            // objects after it; the payment items are destroyed in the finally whatever happens here.
            var round = 0;

            try
            {
                for (var i = 0; i < built.Count; i++)
                {
                    var item = built[i];
                    var settled = false;

                    try
                    {
                        var placed = false;

                        try
                        {
                            placed = pack.Give(item);
                        }
                        catch (Exception ex)
                        {
                            // A throw after the item went in (it has a container now) is still a hand-over; never pile it twice.
                            placed = item.ContainerId != null;
                            log.Error($"[DYNDUNGEON] {bound} peddler: handing {item.Name} to 0x{buyerGuid:X8} threw (placed: {placed})", ex);
                        }

                        if (placed)
                        {
                            result.Given.Add(item);
                            settled = true;
                            continue;
                        }

                        if (round == 0)
                            round = bound.NextDealRound();

                        if (PileAdd(bound, buyerGuid, new HeldPileItem(item, null, round, null, ThreadPeddlerPayment.PurchaseFixedOrderBase + i, peddlerPurchase: true)))
                        {
                            result.ToPile.Add(item);
                            settled = true;
                            log.Warn($"[DYNDUNGEON] {bound} peddler: 0x{buyerGuid:X8}'s pack refused {item.Name} after payment; it waits in their Thread Cache pile");
                        }
                        else
                        {
                            // Only once the run has Ended, when the run-end drain has destroyed the pile it would have joined.
                            settled = true;
                            log.Error($"[DYNDUNGEON] {bound} peddler: 0x{buyerGuid:X8}'s pack and pile both refused {item.Name} after payment (the run has ended); destroyed with the pile");
                            DestroyAll(bound, new List<WorldObject> { item });
                        }
                    }
                    catch (Exception ex)
                    {
                        log.Error($"[DYNDUNGEON] {bound} peddler: delivering {item?.Name} to 0x{buyerGuid:X8} after payment threw; rescuing it into their pile", ex);

                        if (!settled)
                            RescueToPile(bound, buyerGuid, item, i, ref round, result);
                    }
                }
            }
            finally
            {
                // 6. The payment, outside the run's lock.
                foreach (var held in removed)
                {
                    try
                    {
                        if (held.Item != null && !held.Item.IsDestroyed)
                            held.Item.Destroy();
                    }
                    catch (Exception ex)
                    {
                        log.Error($"[DYNDUNGEON] {bound} peddler: destroying a payment item threw", ex);
                    }
                }
            }

            lock (gate)
            {
                if (shops.TryGetValue(buyerGuid, out var shop))
                {
                    shop.LastBuyAt = Clock();
                    shop.OutOfRangeSince = null;
                }
            }

            return result;
        }

        /// <summary>
        /// Best effort for a paid object whose hand-over threw: left alone when it is already somewhere (destroyed, in a
        /// container, or in the pile), otherwise added to the buyer's pile through the run directly (not the
        /// <see cref="PileAdd"/> seam, which may be what threw). Never throws; a failure is logged.
        /// </summary>
        private static void RescueToPile(ThreadDungeonRun bound, uint buyerGuid, WorldObject item, int index, ref int round, BuyResult result)
        {
            try
            {
                if (item == null || item.IsDestroyed)
                    return;

                if (item.ContainerId != null)
                {
                    if (!result.Given.Contains(item))
                        result.Given.Add(item);

                    return;
                }

                if (bound.HeldPileContains(buyerGuid, o => ReferenceEquals(o, item)))
                {
                    if (!result.ToPile.Contains(item))
                        result.ToPile.Add(item);

                    return;
                }

                if (round == 0)
                    round = bound.NextDealRound();

                if (bound.AddToHeldPile(buyerGuid, new HeldPileItem(item, null, round, null, ThreadPeddlerPayment.PurchaseFixedOrderBase + index, peddlerPurchase: true)))
                    result.ToPile.Add(item);
                else
                    log.Error($"[DYNDUNGEON] {bound} peddler: rescuing {item.Name} into 0x{buyerGuid:X8}'s pile was refused (the run has ended)");
            }
            catch (Exception ex)
            {
                log.Error($"[DYNDUNGEON] {bound} peddler: rescuing {item?.Name} into 0x{buyerGuid:X8}'s pile threw", ex);
            }
        }

        /// <summary>Destroys built objects nobody holds (a refused buy). Never throws.</summary>
        private static void DestroyAll(ThreadDungeonRun bound, List<WorldObject> objects)
        {
            foreach (var item in objects)
            {
                try
                {
                    if (item != null && !item.IsDestroyed)
                        item.Destroy();
                }
                catch (Exception ex)
                {
                    log.Error($"[DYNDUNGEON] {bound} peddler: destroying an unsold object threw", ex);
                }
            }

            objects.Clear();
        }

        /// <summary>
        /// The pre-check for the one-per-player cap, before the records are expanded: per capped entry id, what the buyer
        /// has bought plus the amounts in the basket. Long sums, so a huge amount cannot overflow. Not authoritative: the
        /// charge re-checks under the run's lock.
        /// </summary>
        private static bool ExceedsOnePerPlayer(ThreadDungeonRun bound, uint buyerGuid, List<(PeddlerPurchase Purchase, string Name, int Amount)> resolved)
        {
            var capped = bound.PeddlerPlan?.Config.OnePerPlayer;

            if (capped == null || capped.Count == 0 || !resolved.Any(r => capped.Contains(r.Purchase.EntryId)))
                return false;

            return PeddlerOnePerPlayer.Exceeds(capped, bound.PeddlerBought(buyerGuid), resolved.Select(r => (r.Purchase.EntryId, (long)r.Amount)));
        }

        /// <summary>The refusal line for a buy outcome; null for a buy.</summary>
        internal static string RefusalLine(BuyOutcome outcome) => outcome switch
        {
            BuyOutcome.Closed => ClosedMessage,
            BuyOutcome.NotMember => NonMemberMessage,
            BuyOutcome.NotTrading => NotTradingMessage,
            BuyOutcome.Invalid => InvalidBuyMessage,
            BuyOutcome.Unavailable => InvalidBuyMessage,
            BuyOutcome.Insufficient => InsufficientMessage,
            BuyOutcome.NoSeat => NoSeatMessage,
            BuyOutcome.LimitReached => LimitReachedMessage,
            BuyOutcome.TooEncumbered => TooEncumberedMessage,
            BuyOutcome.NoPackSpace => NoPackSpaceMessage,
            BuyOutcome.NoContainerSlots => NoContainerSlotsMessage,
            _ => null,
        };

        /// <summary>Is this a pack refusal, sent as the retail vendor sends it (a transient string)?</summary>
        internal static bool IsPackRefusal(BuyOutcome outcome)
            => outcome == BuyOutcome.TooEncumbered || outcome == BuyOutcome.NoPackSpace || outcome == BuyOutcome.NoContainerSlots;

        /// <summary>
        /// The buy (PersonalVendor / SalvageRollerVendor pattern: never calls base, so FinalizeBuyTransaction, and with it
        /// AnalyticsManager.RecordVendorBuy, never runs for the peddler). Success: the purchase is already in the pack
        /// (<see cref="TryBuy"/>); the pickup sound, one outcome line on the run, the purchase line, the panel re-sent
        /// (ApproachVendor with the amount spent, as Player.FinalizeBuyTransaction sends it, so the stall re-filters to the
        /// new balance), then, only when the buyer is left with nothing they can buy (<see cref="RowsFor"/> empty), the
        /// "Finished trading?" question; returns true. With rows left nothing is asked: the shop stays open and ends by
        /// walk-away, idle, /spawncache, logout or the run ending. Refusal: the matching line (a pack refusal as the retail
        /// transient string), returns false, and Player.HandleActionBuyItem re-sends the panel itself.
        /// </summary>
        public override bool BuyItems_ValidateTransaction(List<ItemProfile> itemProfiles, Player player)
        {
            if (player == null)
                return false;

            var bound = Run;
            var inside = bound != null && InsideCheck(bound, player);
            var result = TryBuy(player.Guid.Full, inside, itemProfiles, () => ReceiverOf?.Invoke(player));

            if (result.Outcome != BuyOutcome.Bought)
            {
                if (IsPackRefusal(result.Outcome))
                    TellTransient(player, RefusalLine(result.Outcome));
                else
                    Tell(player, RefusalLine(result.Outcome));

                return false;
            }

            var spent = (uint)Math.Min(result.Total, uint.MaxValue);
            var line = string.Format(CultureInfo.InvariantCulture, "peddler: {0} bought {1} for {2} unit(s) ({3} record(s)); {4} payment item(s) taken from the pile; {5} object(s) to the pack, {6} to the pile; balance now {7}",
                player.Name, result.Label, result.Total, result.Records.Count, result.PaymentItems, result.Given.Count, result.ToPile.Count, bound.PeddlerBalance(player.Guid.Full));

            bound.RecordEncounterOutcome(line);
            log.Info($"[DYNDUNGEON] {bound} {line}");

            player.Session?.Network.EnqueueSend(new GameMessageSound(player.Guid, Sound.PickUpItem));

            foreach (var purchaseLine in PurchaseLines(result.Bought))
                Tell(player, purchaseLine);

            ShowPanel(player, VendorType.Buy, spent);

            // Owner ruling 2026-11-20: several purchases per visit. The question goes out only once nothing more can be bought.
            if (ShouldAskFinished(player))
                AskFinished(player);

            return true;
        }

        #endregion

        #region Done

        /// <summary>
        /// Sends the "Finished trading?" question, spam-guarded: when this peddler's own question is already pending nothing
        /// is sent; an unrelated pending Yes_No earns the ConfirmationInProgress error.
        /// </summary>
        internal void AskFinished(Player player)
        {
            if (player == null || FinishedAsk == null)
                return;

            if (FinishedAsk(player, FinishedPromptMessage))
                return;

            if (AskFailed != null)
                AskFailed(player);
            else
                player.SendWeenieError(WeenieError.ConfirmationInProgress);
        }

        private bool DefaultFinishedAsk(Player player, string text)
        {
            if (player.Session == null)
                return true;

            var confirmation = new Confirmation_PeddlerShop(player.Guid, this);
            var sent = player.ConfirmationManager.EnqueueSend(confirmation, text);

            Confirmation pending = null;

            if (!sent)
                player.ConfirmationManager.TryGetPending(ConfirmationType.Yes_No, out pending);

            return AskHandled(sent, pending, this);
        }

        /// <summary>
        /// True when the question is up: it was just sent, or the player's pending Yes_No is already this peddler's own
        /// (a repeat buy stays silent). False only when an unrelated Yes_No is open.
        /// </summary>
        internal static bool AskHandled(bool sent, Confirmation pending, ThreadPeddlerVendor peddler)
            => sent || (pending is Confirmation_PeddlerShop ours && ReferenceEquals(ours.Peddler, peddler));

        /// <summary>
        /// Withdraws this peddler's own pending "Finished trading?" question from the member, if one is open
        /// (ConfirmationManager.TryCancel; an unrelated Yes_No is left alone). Called on every release. Never throws.
        /// </summary>
        internal void WithdrawFinishedQuestion(uint memberGuid)
        {
            try
            {
                var player = PlayerLookup(memberGuid);
                var manager = player?.ConfirmationManager;

                if (manager != null && manager.TryGetPending(ConfirmationType.Yes_No, out var pending)
                    && pending is Confirmation_PeddlerShop ours && ReferenceEquals(ours.Peddler, this))
                    manager.TryCancel(pending);
            }
            catch (Exception ex)
            {
                log.Error($"[DYNDUNGEON] peddler: withdrawing the Finished question from 0x{memberGuid:X8} threw", ex);
            }
        }

        /// <summary>
        /// One member is done, from any source (the Yes, the poll, a test). The run's latch decides
        /// (ThreadPeddlerShop.Release); this peddler's own record is marked done either way. True only for the release that
        /// won the latch.
        /// </summary>
        internal bool Finish(uint memberGuid, string why)
        {
            ThreadDungeonRun bound;

            lock (gate)
            {
                bound = run;

                if (shops.TryGetValue(memberGuid, out var shop))
                    shop.Done = true;
            }

            if (bound == null || !ThreadPeddlerShop.Release(bound, memberGuid, why))
                return false;

            // Release withdraws through the run's NPC; this covers a peddler the run does not (or no longer) point at.
            WithdrawFinishedQuestion(memberGuid);
            return true;
        }

        /// <summary>The Yes half of the question, on the player's queue.</summary>
        internal void FinishFromPrompt(Player player)
        {
            if (player != null)
                Finish(player.Guid.Full, "answered Yes");
        }

        /// <summary>
        /// The time one member becomes idle-done: idleSeconds after presentation with no Use, otherwise idleSeconds after the
        /// later of the last Use and the last buy, capped at <paramref name="capUnix"/>. Pure.
        /// </summary>
        internal static double IdleDeadline(MemberShop shop, double idle, double capUnix)
        {
            var since = shop.LastUseAt == null ? shop.PresentedAt : Math.Max(shop.LastUseAt.Value, shop.LastBuyAt ?? double.MinValue);
            return Math.Min(since + idle, capUnix);
        }

        /// <summary>The run's expiry minus the margin, unix seconds: no shop deadline runs past it.</summary>
        internal static double ExpiryCapUnix(ThreadDungeonRun run)
            => (run.ExpiresUtc - DateTime.UnixEpoch).TotalSeconds - ExpiryMarginSeconds;

        /// <summary>
        /// One pass over the armed members, on the peddler's landblock queue: decides who is done now (left or logged out,
        /// walked away, idle) and releases them, outside the gate. Returns the releases it made (the reason per member).
        /// </summary>
        internal List<(uint Member, string Why)> PollOnce()
        {
            var due = new List<(uint, string)>();
            ThreadDungeonRun bound;
            List<uint> armed;

            lock (gate)
            {
                bound = run;

                if (bound == null || departed)
                    return due;

                armed = shops.Where(kv => !kv.Value.Done).Select(kv => kv.Key).ToList();
            }

            var now = Clock();
            var cap = ExpiryCapUnix(bound);

            foreach (var guid in armed)
            {
                // Released elsewhere (/spawncache, the run's own close): mirror it, nothing to request.
                if (!bound.IsPeddlerShopOpen(guid))
                {
                    lock (gate)
                    {
                        if (shops.TryGetValue(guid, out var s))
                            s.Done = true;
                    }

                    continue;
                }

                var player = PlayerLookup(guid);
                var inside = player != null && InsideCheck(bound, player);
                var inRange = inside && RangeCheck(player);
                string why = null;

                lock (gate)
                {
                    if (!shops.TryGetValue(guid, out var shop) || shop.Done)
                        continue;

                    if (!inside)
                        why = "logged out or left the run";
                    else if (shop.LastUseAt != null)
                    {
                        if (inRange)
                            shop.OutOfRangeSince = null;
                        else
                        {
                            shop.OutOfRangeSince ??= now;

                            if (now - shop.OutOfRangeSince.Value >= walkAwaySeconds)
                                why = "walked away";
                        }
                    }

                    if (why == null && now >= IdleDeadline(shop, idleSeconds, cap))
                        why = now >= cap ? "the thread is about to close" : "idle";
                }

                if (why != null)
                {
                    Finish(guid, why);
                    due.Add((guid, why));
                }
            }

            return due;
        }

        #endregion

        #region Poll and departure

        /// <summary>
        /// Starts the poll, once: every <see cref="PollIntervalSeconds"/> on the peddler's own landblock queue it runs
        /// <see cref="PollOnce"/> and then <see cref="CheckDeparture"/>. Called by ThreadPeddlerShop.Present after EnterWorld.
        /// </summary>
        internal void StartWatch()
        {
            lock (gate)
            {
                if (polling)
                    return;

                polling = true;
                lastPollAt = Clock();
            }

            ScheduleTick();
        }

        private void ScheduleTick()
        {
            if (CurrentLandblock == null)
                return;

            var chain = new ActionChain();
            chain.AddDelaySeconds(PollIntervalSeconds);
            chain.AddAction(this, () =>
            {
                if (IsDestroyed)
                    return;

                LastPollAt = Clock();

                try
                {
                    PollOnce();
                }
                catch (Exception ex)
                {
                    log.Error($"[DYNDUNGEON] {Run} peddler poll threw", ex);
                }

                if (!CheckDeparture() && !IsDestroyed)
                    ScheduleTick();
            });
            chain.EnqueueChain();
        }

        /// <summary>Why the peddler should leave now, or null to stay. Pure over the run state and the armed members.</summary>
        internal string DepartureReason()
        {
            lock (gate)
            {
                if (departed)
                    return null;

                if (run == null)
                    return "unbound";

                var state = run.State;

                if (state != ThreadDungeonRunState.Cleared)
                    return $"the run is {state}";

                // Created, but nobody was ever armed (the open failed right after the NPC appeared): leave now.
                if (shops.Count == 0)
                    return "nobody to trade with";

                if (shops.All(kv => kv.Value.Done || !run.IsPeddlerShopOpen(kv.Key)))
                    return "every member finished";

                return null;
            }
        }

        /// <summary>The peddler's landblock queue: leaves when <see cref="DepartureReason"/> says so. True once departed.</summary>
        internal bool CheckDeparture()
        {
            if (IsDestroyed)
                return true;

            var reason = DepartureReason();

            if (reason == null)
                return Departed;

            Depart(reason);
            return true;
        }

        /// <summary>
        /// Leaves, once: marks the peddler departed (no Use or buy is served after this), closes the run's peddler (so no
        /// member is presented again, and any shop still open is released and its placement requested), tells the armed
        /// members (unless the run has Ended, when its copy is being torn down), records it, and destroys the peddler, whose
        /// vendor branch destroys every display. The client closes an open vendor panel itself when the vendor is destroyed.
        /// </summary>
        internal void Depart(string reason)
        {
            ThreadDungeonRun bound;
            List<uint> armed;

            lock (gate)
            {
                if (departed)
                    return;

                departed = true;
                bound = run;
                armed = shops.Keys.ToList();

                foreach (var shop in shops.Values)
                    shop.Done = true;
            }

            try
            {
                if (bound != null)
                {
                    var released = bound.ClosePeddler();

                    foreach (var guid in armed)
                        WithdrawFinishedQuestion(guid);

                    if (bound.State == ThreadDungeonRunState.Cleared)
                    {
                        foreach (var guid in released)
                        {
                            bound.RecordEncounterOutcome($"peddler: 0x{guid:X8} done (the peddler left)");
                            ThreadPeddlerShop.RequestAfterRelease(bound, guid);
                        }
                    }
                }

                if (bound != null && bound.State != ThreadDungeonRunState.Ended)
                {
                    foreach (var guid in armed)
                        Tell(PlayerLookup(guid), DepartureMessage);
                }

                bound?.RecordEncounterOutcome($"peddler: departed ({reason})");
                log.Info($"[DYNDUNGEON] {bound} peddler departed ({reason})");
            }
            catch (Exception ex)
            {
                log.Error($"[DYNDUNGEON] {bound} peddler departure threw", ex);
            }

            if (!IsDestroyed)
                Destroy();
        }

        /// <summary>The run ended (PeddlerEncounterType.OnRunEnded): memory only. No further Use or buy is served; EndRun destroys the object.</summary>
        internal void MarkRunEnded()
        {
            lock (gate)
                departed = true;
        }

        #endregion

        /// <summary>One line to <paramref name="player"/>, skipped when there is no session to send it on.</summary>
        internal void Tell(Player player, string text)
        {
            if (player == null || string.IsNullOrEmpty(text))
                return;

            Told?.Invoke(player, text);

            player.Session?.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));
        }

        /// <summary>One retail-style transient line (Vendor.BuyItems_ValidateTransaction's pack refusals), skipped without a session.</summary>
        internal void TellTransient(Player player, string text)
        {
            if (player == null || string.IsNullOrEmpty(text))
                return;

            Told?.Invoke(player, text);

            if (player.Session != null)
                player.Session.Network.EnqueueSend(new ACE.Server.Network.GameEvent.Events.GameEventCommunicationTransientString(player.Session, text));
        }
    }

    /// <summary>
    /// The Wandering Peddler's "Finished trading?" question, sent after a buy that leaves the buyer nothing more to buy. Yes, on time, releases the member (their
    /// Thread Cache forms); No, a timeout and a late Yes all leave them shopping. ConfirmationManager's abort only sends a
    /// message and leaves the entry pending, so a Yes after the client's timeout still arrives here:
    /// <see cref="Confirmation_ThreadGroupStart.IsLate"/> treats it as No.
    /// </summary>
    public class Confirmation_PeddlerShop : Confirmation
    {
        internal ThreadPeddlerVendor Peddler { get; }

        internal DateTime SentUtc { get; }

        internal Confirmation_PeddlerShop(ObjectGuid playerGuid, ThreadPeddlerVendor peddler)
            : base(playerGuid, ConfirmationType.Yes_No)
        {
            Peddler = peddler;
            SentUtc = DateTime.UtcNow;
        }

        public override void ProcessConfirmation(bool response, bool timeout = false)
        {
            var player = Player;
            if (player == null)
                return;

            var late = Confirmation_ThreadGroupStart.IsLate(SentUtc, DateTime.UtcNow, timeout);

            // Hop onto the player's own queue before touching the peddler, as Confirmation_KnotLower does.
            Dispatch(response, timeout, late, player, Peddler, work => player.EnqueueAction(new ActionEventDelegate(work)));
        }

        /// <summary>The decision, apart from the queue: only an on-time Yes reaches <see cref="ThreadPeddlerVendor.FinishFromPrompt"/>.</summary>
        internal static void Dispatch(bool response, bool timeout, bool isLate, Player player, ThreadPeddlerVendor peddler, Action<Action> enqueue)
        {
            if (!response || timeout || isLate || peddler == null)
                return;

            enqueue(() => peddler.FinishFromPrompt(player));
        }
    }
}
