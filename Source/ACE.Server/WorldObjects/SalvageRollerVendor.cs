using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

using log4net;

using ACE.Common;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Managers.Market;
using ACE.Server.SalvageRoller;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// The smallest thing the station needs to know about a rolled candidate: its sequence number in the
    /// batch. SalvageRollerCandidate (Docs/SalvageRoller/DESIGN.md 5.5) implements this so
    /// <see cref="SalvageRollerVendor.SetRows{T}"/> can take it directly, and the caller supplies the
    /// function that turns one candidate into its display object.
    /// </summary>
    internal interface ISalvageRollerRowCandidate
    {
        /// <summary>The candidate's sequence number in its batch (candidate k = #k). Unique within one batch.</summary>
        int Seq { get; }
    }

    /// <summary>
    /// Salvage Roller (Docs/SalvageRoller/DESIGN.md 5.1, 5.4): the ONE placed station (wcid 1007800,
    /// PropertyBool.SalvageRollerStation) that serves every player their own vendor panel. A player sells a
    /// target and Hammers to it, and later "buys" exactly one rolled candidate from it.
    ///
    /// Per-viewer views: the station keeps one <see cref="View"/> per player guid (rows, display objects, search
    /// filter, last-touch time). A view is created by the station's Use (<see cref="SalvageRollerStation.Open"/>), or by
    /// a fill (<see cref="SetRows{T}"/>) for a seller whose roll confirmation was answered after their view had ended, or by a
    /// sale (<see cref="TryEnsureSellerView"/>) from a player whose view was swept while their panel on this station stayed
    /// open. An approach never creates one.
    /// GameEventApproachVendor serializes the viewer's rows through the Vendor viewer seam
    /// (<see cref="GetItemsForSaleCount(Player)"/>, <see cref="ForEachItemFor"/>), and every buy and sell resolves
    /// against the acting player's view. Every viewer-less member fails closed: it emits nothing, counts 0 and
    /// resolves nothing, so a caller that never names a viewer can never see anybody's rows.
    ///
    /// A SIBLING of <see cref="PersonalVendor"/>, not a subclass: PersonalVendor is tied to the account vault's
    /// store, and nothing here touches a store. It copies PersonalVendor's invariants (DESIGN section 4):
    ///  - ethereal and unattackable, set in code as well as in the weenie;
    ///  - empty <see cref="LoadInventory"/>, inert <see cref="RotUniques"/> and <see cref="PrepareResetToHome"/>
    ///    (the latter also because a placed station has no Home, and CheckResetToHome dereferences it);
    ///  - every cost is zero;
    ///  - a view check on every transaction, not only on use.
    ///
    /// Single-customer Vendor state is never touched: <see cref="ActOnUse"/> does not call base, so
    /// Vendor.lastPlayerInfo and CheckClose never run, and <see cref="Rotate"/> is inert, so one player's Use never
    /// turns the station away from another.
    ///
    /// Display rows: every candidate is shown through a disposable display object. Every viewer's displays are
    /// ALSO in <see cref="Vendor.DefaultItemsForSale"/>, the global ownership registry, so WorldObject.Destroy's
    /// vendor branch reaches all of them and the duplicate-guid check is global. That registry is never a lookup
    /// path: the per-view index is. Invariant (pinned by the tests): the keys of DefaultItemsForSale equal the
    /// union of every view's display guids. Nothing real (the escrowed target, a Hammer) is ever put in either
    /// base dictionary. The search filter is display-only and per view: it decides which rows the viewer is
    /// shown and never removes a row, so a row the player can no longer see is still a valid pick.
    ///
    /// Views end on logout and in the sweep (<see cref="SweepViews"/>: on the heartbeat, and lazily on every Use and
    /// approach) when the viewer is offline, has left the station's landblock and its neighbours, or has been idle
    /// longer than salvage_roller_window_rot_seconds. A delivered take and a new SetRows destroy only that viewer's
    /// displays and keep the view. CheckClose never running also means nothing clears player.LastOpenedContainerId
    /// for this station except opening another vendor or container, which <see cref="TryEnsureSellerView"/> relies on.
    /// </summary>
    public class SalvageRollerVendor : Vendor
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>Told to a player with no live view who sells to or buys from the station. Provisional text.</summary>
        internal const string NoViewMessage = "Use the Salvage Roller first.";

        /// <summary>
        /// Told instead of SalvageRollerEligibility.BatchExistsMessage when a sale from an UNFILLED view (one a sale made,
        /// see <see cref="TryEnsureSellerView"/>) is refused because the seller already has a batch: their results are not
        /// on this panel, and only a Use shows them. Provisional text: the owner reviews it.
        /// </summary>
        internal const string ResultsWaitingMessage = "You already have results waiting. Use the Salvage Roller to see them.";

        /// <summary>Told on Use when the station is not open for business (a vendor row is missing). Provisional text.</summary>
        internal const string NotOpenMessage = "The Salvage Roller is not open for business.";

        /// <summary>A buy that is not exactly one row with an amount of one.</summary>
        internal const string TakeExactlyOneMessage = "Take exactly one result at a time.";

        /// <summary>A buy naming a row the buyer's own view does not hold.</summary>
        internal const string UnknownRowMessage = "That result is no longer on offer.";

        /// <summary>Any sell line was dropped before the basket reached the station.</summary>
        internal const string BasketLineDroppedMessage = "The Salvage Roller takes the whole basket or nothing. One of those items could not be offered, so nothing was taken.";

        /// <summary>Kept for the record of step 6; the default <see cref="TakeHandler"/> now runs the take.</summary>
        internal const string TakeUnavailableMessage = "The Salvage Roller cannot hand over a result yet.";

        /// <summary>The idle view lifetime, in seconds, when salvage_roller_window_rot_seconds is not positive.
        /// Short and safe, never "never expire" - the same rule and value as the mule vendor's RestampRot.</summary>
        internal const double MinimumViewSecondsOnMisconfiguration = 60;

        /// <summary>The config key the idle view lifetime is read from. The name predates the shared station.</summary>
        internal const string ViewLifetimeKey = "salvage_roller_window_rot_seconds";

        /// <summary>
        /// Step 7's seam for the take itself (DESIGN 5.4 step 7, items 2 onward). Called by
        /// <see cref="BuyItems_ValidateTransaction"/> only after the view, the one-row shape and the row
        /// lookup have all passed, with the chosen candidate's sequence number. Its return value is the buy's.
        /// A test that replaces it must restore it.
        /// </summary>
        internal static Func<Player, SalvageRollerVendor, int, bool> TakeHandler = DefaultTakeHandler;

        /// <summary>The whole-basket check <see cref="TryValidateSellBasket"/> delegates to once its own shape checks pass.</summary>
        internal delegate bool SellBasketVerifier(Player player, IReadOnlyList<WorldObject> offered, int requestedCount, out string refusal);

        /// <summary>The refusal when no basket verifier is installed (never in production).</summary>
        internal const string BasketUnavailableMessage = "The Salvage Roller is not accepting items right now.";

        /// <summary>Told to the seller when an item the station could not keep also could not be put back in their pack.</summary>
        internal const string HandBackFailedMessage = "The Salvage Roller could not return one of your items. Contact an admin; it has been logged.";

        /// <summary>The item IS back in the pack, but saving it there threw: distinct from a failed return, because nothing is lost yet.</summary>
        internal const string HandBackUnsavedMessage = "One of your items is back in your pack, but the server could not save it there yet. It will be saved with your character; if it is missing after you log in again, contact an admin.";

        /// <summary>
        /// Seam over SalvageRollerEligibility.VerifyBasket, so a test can drive both outcomes of the sell hook
        /// without depending on the eligibility rules themselves. Production never replaces it. A test that
        /// replaces it must restore it.
        /// </summary>
        internal static SellBasketVerifier BasketVerifier = DefaultBasketVerifier;

        private sealed class Row
        {
            public Row(int seq, WorldObject display)
            {
                Seq = seq;
                Display = display;
            }

            public int Seq { get; }
            public WorldObject Display { get; }
        }

        /// <summary>One player's panel at this station.</summary>
        private sealed class View
        {
            public View(uint viewerGuid) => ViewerGuid = viewerGuid;

            public uint ViewerGuid { get; }

            /// <summary>Every row, in the order the caller supplied (the display order). Display objects only.</summary>
            public List<Row> Rows { get; } = new List<Row>();

            /// <summary>Display guid to candidate sequence number, for every row whether or not the filter shows it.</summary>
            public Dictionary<ObjectGuid, int> SeqByDisplay { get; } = new Dictionary<ObjectGuid, int>();

            /// <summary>The rows the filter shows, in row order. Recomputed by every SetRows and SetSearchFilter.</summary>
            public List<Row> VisibleRows { get; set; } = new List<Row>();

            /// <summary>The live search filter, or null to show every row. Display-only.</summary>
            public Regex SearchFilter { get; set; }

            /// <summary>Unix time of the last Use, approach or SetRows; the idle sweep measures from it.</summary>
            public double LastTouchedUnix { get; set; }

            /// <summary>
            /// True for a view a sale made (<see cref="TryEnsureSellerView"/>): it skipped the Use, so a batch the player
            /// holds was never shown on it. Cleared by a Use (<see cref="OpenView"/>) and by every fill (<see cref="SetRows{T}"/>).
            /// </summary>
            public bool Unfilled { get; set; }
        }

        /// <summary>Every live view, by viewer guid. Every read and write takes <see cref="viewsLock"/>.</summary>
        private readonly Dictionary<uint, View> views = new Dictionary<uint, View>();

        /// <summary>
        /// Guards <see cref="views"/> AND this station's use of DefaultItemsForSale. Inbound messages run serially on
        /// the world thread, but the threads that run logout and the escrow landing callbacks were not traced, so
        /// every mutation takes it.
        /// </summary>
        private readonly object viewsLock = new object();

        /// <summary>
        /// A new biota be created taking all of its values from weenie.
        /// </summary>
        public SalvageRollerVendor(Weenie weenie, ObjectGuid guid) : base(weenie, guid)
        {
            SetEphemeralValues();
        }

        /// <summary>
        /// Restore a WorldObject from the database. Implemented so a station biota never comes back as a plain
        /// retail Vendor, which would destroy every sold item for a coin.
        /// </summary>
        public SalvageRollerVendor(Biota biota) : base(biota)
        {
            SetEphemeralValues();
        }

        /// <summary>
        /// The mule vendor's anti-grief pair (PersonalVendor.SetEphemeralValues): never a body-blocker, never
        /// an aggro target. Set here AND in the weenie, so a content edit alone cannot undo it.
        /// </summary>
        private void SetEphemeralValues()
        {
            Ethereal = true;
            Attackable = false;
        }

        #region PersonalVendor invariants (DESIGN section 4)

        /// <summary>The panel is built from <see cref="SetRows{T}"/>, never from a create list.</summary>
        protected override void LoadInventory()
        {
        }

        /// <summary>UniqueItemsForSale is never populated here, so the retail rot-and-destroy path has nothing to do.</summary>
        protected override void RotUniques()
        {
        }

        /// <summary>
        /// Inert: the station never walks anywhere, and it has no Home, which Vendor.CheckResetToHome would
        /// dereference.
        /// </summary>
        protected override void PrepareResetToHome()
        {
        }

        public override uint GetSellCost(WorldObject item) => 0;

        public override uint GetSellCost(Weenie item) => 0;

        public override int GetBuyCost(WorldObject item) => 0;

        public override int GetBuyCost(Weenie item) => 0;

        public override int CalculatePayoutCoinAmount(Dictionary<uint, WorldObject> items) => 0;

        /// <summary>The station pays nothing: IsSellable, Value &lt; 1, Retained and the payout leg are bypassed.</summary>
        public override bool SellBypassesPriceGates => true;

        /// <summary>
        /// The item returns only to the character that sold it, so the Attuned refusal's cross-account reason
        /// does not apply, and the manual salvage path allows attuned targets (DESIGN 5.3).
        /// </summary>
        public override bool SellBypassesAttunedRefusal => true;

        /// <summary>
        /// Refuses everything: display objects enter only through <see cref="SetRows{T}"/>. Loud, because a
        /// caller reaching this (a shop generator, say) would be putting something into a dictionary that
        /// WorldObject.Destroy walks.
        /// </summary>
        public override void AddDefaultItem(WorldObject item)
        {
            log.Error($"[SALVAGE ROLLER] {Name}: refused to add 0x{item?.Guid.Full ?? 0:X8} ({item?.Name}) through AddDefaultItem. Display rows are set only by SetRows.");
        }

        #endregion

        #region Use and approach (shared station)

        /// <summary>
        /// Use: the busy check, then the station's open flow. Deliberately does NOT call base: Vendor.ActOnUse keeps
        /// one lastPlayerInfo and runs CheckClose for a single customer, and rotates toward the user.
        /// </summary>
        public override void ActOnUse(WorldObject wo)
        {
            if (!(wo is Player player))
                return;

            // Vendor's constructor sets OpenForBusiness from ValidateVendorRequirements: a station weenie missing any
            // vendor row (int 74/75/76, float 37/38, the coin weenie) is closed. That is the new-image-plus-old-content
            // deploy order, and it must open nothing.
            if (!OpenForBusiness)
            {
                Tell(player, NotOpenMessage);
                return;
            }

            if (player.IsBusy)
            {
                player.SendWeenieError(WeenieError.YoureTooBusy);
                return;
            }

            SalvageRollerStation.Open(player, this);
        }

        /// <summary>Inert: a shared station never turns toward one customer.</summary>
        public override float Rotate(WorldObject target) => 0f;

        /// <summary>
        /// Sends the viewer their own panel. Sweeps first and touches the viewer's view, if there is one; it never
        /// creates a view, so a player who never used the station is shown an empty panel.
        /// </summary>
        public override void ApproachVendor(Player player, VendorType action = VendorType.Undef, uint altCurrencySpent = 0)
        {
            if (player == null)
                return;

            SweepViews(Time.GetUnixTime());

            TouchView(player.Guid.Full);

            if (player.Session == null)
                return;

            WithApproachSnapshot(player, () =>
            {
                base.ApproachVendor(player, action, altCurrencySpent);
                return 0;
            });
        }

        /// <summary>One approach's rows, so its count and its serialized rows come from the same read.</summary>
        private sealed class ApproachSnapshot
        {
            public SalvageRollerVendor Station;
            public uint Viewer;
            public List<WorldObject> Rows;
        }

        /// <summary>
        /// The snapshot of the approach running on THIS thread, or null. GameEventApproachVendor reads
        /// GetItemsForSaleCount(viewer) and then ForEachItemFor(viewer) synchronously inside Vendor.ApproachVendor; with
        /// a snapshot in place both read the same list, so the count written always equals the rows serialized even if
        /// another thread refills or drops the view in between.
        /// </summary>
        [ThreadStatic]
        private static ApproachSnapshot approachSnapshot;

        /// <summary>
        /// Runs <paramref name="body"/> with the viewer's visible rows snapshotted once, under the lock, for both viewer
        /// overloads to read. Restores whatever snapshot was in place before (an approach inside an approach).
        /// </summary>
        internal T WithApproachSnapshot<T>(Player viewer, Func<T> body)
        {
            var previous = approachSnapshot;

            approachSnapshot = new ApproachSnapshot { Station = this, Viewer = viewer.Guid.Full, Rows = VisibleDisplays(viewer.Guid.Full) };

            try
            {
                return body();
            }
            finally
            {
                approachSnapshot = previous;
            }
        }

        /// <summary>The viewer's visible display objects, read once under the lock. Empty with no view.</summary>
        private List<WorldObject> VisibleDisplays(uint viewerGuid)
        {
            lock (viewsLock)
                return views.TryGetValue(viewerGuid, out var view) ? view.VisibleRows.Select(r => r.Display).ToList() : new List<WorldObject>();
        }

        /// <summary>The rows a viewer overload serves: the running approach's snapshot for that viewer, or a fresh read.</summary>
        private List<WorldObject> RowsFor(Player viewer)
        {
            if (viewer == null)
                return new List<WorldObject>();

            var snapshot = approachSnapshot;

            if (snapshot != null && ReferenceEquals(snapshot.Station, this) && snapshot.Viewer == viewer.Guid.Full)
                return snapshot.Rows;

            return VisibleDisplays(viewer.Guid.Full);
        }

        /// <summary>The base heartbeat, then the view sweep.</summary>
        public override void Heartbeat(double currentUnixTime)
        {
            base.Heartbeat(currentUnixTime);

            SweepViews(currentUnixTime);
        }

        #endregion

        #region Views

        /// <summary>The idle view lifetime: salvage_roller_window_rot_seconds, or 60 s when that is not positive.</summary>
        internal static double ViewLifetimeSeconds()
        {
            var configured = PropertyManager.GetLong(ViewLifetimeKey).Item;

            return configured > 0 ? configured : MinimumViewSecondsOnMisconfiguration;
        }

        /// <summary>True when <paramref name="viewerGuid"/> has a live view here.</summary>
        internal bool HasView(uint viewerGuid)
        {
            lock (viewsLock)
                return views.ContainsKey(viewerGuid);
        }

        internal bool HasView(Player viewer) => viewer != null && HasView(viewer.Guid.Full);

        /// <summary>How many views this station holds.</summary>
        internal int ViewCount
        {
            get
            {
                lock (viewsLock)
                    return views.Count;
            }
        }

        /// <summary>Creates the viewer's view if there is none, and touches it. Only the station's Use calls this.</summary>
        internal void OpenView(uint viewerGuid)
        {
            lock (viewsLock)
            {
                var view = GetOrCreateView(viewerGuid);
                view.LastTouchedUnix = Time.GetUnixTime();
                view.Unfilled = false;
            }
        }

        /// <summary>Creates an UNFILLED view for a seller whose view is gone. Only <see cref="TryEnsureSellerView"/> calls this.</summary>
        private void OpenUnfilledView(uint viewerGuid)
        {
            lock (viewsLock)
            {
                if (views.ContainsKey(viewerGuid))
                    return;

                var view = GetOrCreateView(viewerGuid);
                view.LastTouchedUnix = Time.GetUnixTime();
                view.Unfilled = true;
            }
        }

        /// <summary>True when the viewer has a view and it is unfilled (made by a sale, not yet shown by a Use or a fill).</summary>
        internal bool IsUnfilledView(uint viewerGuid)
        {
            lock (viewsLock)
                return views.TryGetValue(viewerGuid, out var view) && view.Unfilled;
        }

        /// <summary>Touches an existing view. Never creates one. Returns whether there was one.</summary>
        internal bool TouchView(uint viewerGuid)
        {
            lock (viewsLock)
            {
                if (!views.TryGetValue(viewerGuid, out var view))
                    return false;

                view.LastTouchedUnix = Time.GetUnixTime();
                return true;
            }
        }

        private View GetOrCreateView(uint viewerGuid)
        {
            if (!views.TryGetValue(viewerGuid, out var view))
            {
                view = new View(viewerGuid);
                views[viewerGuid] = view;
            }

            return view;
        }

        /// <summary>
        /// Replaces every row of <paramref name="viewerGuid"/>'s view, creating the view if there is none. Only that
        /// viewer's previous display objects are destroyed; each candidate is turned into its display object by
        /// <paramref name="materialize"/> (a snapshot of the escrowed target with the candidate's result applied, a
        /// fresh dynamic guid, the "(#k)" name suffix). Rows are shown in the order given, so the caller sorts. A
        /// candidate whose sequence number repeats, or whose display object is null, already a row of ANY view, or
        /// not disposable, is skipped and logged. The view's search filter is re-applied. Returns the number of rows set.
        /// </summary>
        internal int SetRows<T>(uint viewerGuid, IEnumerable<T> candidates, Func<T, WorldObject> materialize) where T : ISalvageRollerRowCandidate
        {
            if (materialize == null)
                throw new ArgumentNullException(nameof(materialize));

            // Materialize OUTSIDE the lock: building a display object runs the world object factory, and nothing of
            // this station's state is read here. The repeated-seq and null refusals need no shared state either.
            var built = new List<(int Seq, WorldObject Display)>();

            if (candidates != null)
            {
                try
                {
                    var seenSeqs = new HashSet<int>();

                    foreach (var candidate in candidates)
                    {
                        if (candidate == null)
                            continue;

                        if (!seenSeqs.Add(candidate.Seq))
                        {
                            log.Error($"[SALVAGE ROLLER] {Name}: candidate #{candidate.Seq} appears twice for viewer 0x{viewerGuid:X8}; the second is not shown.");
                            continue;
                        }

                        var display = materialize(candidate);

                        if (display == null)
                        {
                            log.Error($"[SALVAGE ROLLER] {Name}: candidate #{candidate.Seq} for viewer 0x{viewerGuid:X8} produced no display object; that row is not shown.");
                            continue;
                        }

                        built.Add((candidate.Seq, display));
                    }
                }
                catch
                {
                    // The materializer threw part way: nothing is registered yet and the viewer's current rows are untouched.
                    // The displays already built are fresh and unregistered, and Destroy is what recycles their dynamic guids,
                    // so destroy them. Only the fresh ones: anything that is already a row (of any view) or is not disposable
                    // (a real object a buggy materializer returned) is left exactly as it is, as at the lock below.
                    DestroyUnregisteredDisposables(built);
                    throw;
                }
            }

            lock (viewsLock)
            {
                var view = GetOrCreateView(viewerGuid);

                ClearRowsLocked(view);

                view.LastTouchedUnix = Time.GetUnixTime();
                view.Unfilled = false;

                foreach (var (seq, display) in built)
                {
                    // Global, not per view: one display object is never a row twice, in this view or another. A refused
                    // object is NOT destroyed: it is somebody's live row.
                    if (DefaultItemsForSale.ContainsKey(display.Guid))
                    {
                        log.Error($"[SALVAGE ROLLER] {Name}: candidate #{seq} for viewer 0x{viewerGuid:X8} reused display 0x{display.Guid.Full:X8}, which is already a row; it is not shown twice.");
                        continue;
                    }

                    // A display object is disposable by definition, and ClearRows, DropView and WorldObject.Destroy
                    // destroy every row. Refuse anything that looks like a real item - saved to the shard, worn, or
                    // parented somewhere other than this station - so a materializer bug can never hand a real biota
                    // to that destroy path. A refused object is left exactly as it is: destroying it here would be the
                    // very item loss this check exists to prevent.
                    if (!IsDisposableDisplay(display, out var whyNot))
                    {
                        log.Error($"[SALVAGE ROLLER] {Name}: candidate #{seq} for viewer 0x{viewerGuid:X8} produced 0x{display.Guid.Full:X8} ({display.Name}, wcid {display.WeenieClassId}), which {whyNot}; it is not a disposable display object and is not shown.");
                        continue;
                    }

                    // Parented to the station, as the mule vendor's displays are: the vendor root-owner guards in
                    // Player_Inventory key off it, which is what keeps a display object out of a player's pack.
                    display.ContainerId = Guid.Full;

                    DefaultItemsForSale[display.Guid] = display;
                    view.SeqByDisplay[display.Guid] = seq;
                    view.Rows.Add(new Row(seq, display));
                }

                ApplyFilter(view);

                return view.Rows.Count;
            }
        }

        /// <summary>
        /// Destroys each of <paramref name="built"/> that is not registered as a row and is disposable. One destroy that
        /// throws never stops the rest.
        /// </summary>
        private void DestroyUnregisteredDisposables(List<(int Seq, WorldObject Display)> built)
        {
            List<WorldObject> fresh;

            lock (viewsLock)
                fresh = built.Select(b => b.Display).Where(d => !d.IsDestroyed && !DefaultItemsForSale.ContainsKey(d.Guid) && IsDisposableDisplay(d, out _)).ToList();

            foreach (var display in fresh)
            {
                try
                {
                    display.Destroy();
                }
                catch (Exception ex)
                {
                    log.Warn($"[SALVAGE ROLLER] {Name}: destroying unregistered display 0x{display.Guid.Full:X8} after a materializer failure threw.", ex);
                }
            }
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

        /// <summary>Destroys the viewer's display objects and forgets their rows. The view and its filter are kept.</summary>
        internal void ClearRows(uint viewerGuid)
        {
            lock (viewsLock)
            {
                if (views.TryGetValue(viewerGuid, out var view))
                    ClearRowsLocked(view);
            }
        }

        /// <summary>Ends the viewer's view: destroys its display objects, removes it, and with it its filter.</summary>
        internal void DropView(uint viewerGuid)
        {
            lock (viewsLock)
            {
                if (!views.TryGetValue(viewerGuid, out var view))
                    return;

                ClearRowsLocked(view);

                views.Remove(viewerGuid);
            }
        }

        internal void DropView(Player viewer)
        {
            if (viewer != null)
                DropView(viewer.Guid.Full);
        }

        /// <summary>
        /// Called from WorldObject.Destroy, after its vendor branch has destroyed every DefaultItemsForSale entry
        /// (every viewer's displays): forgets every view, empties the registry, and clears each online viewer's slot
        /// when it still points here.
        /// </summary>
        internal void ForgetAllViews()
        {
            List<uint> viewers;

            lock (viewsLock)
            {
                foreach (var view in views.Values)
                    ClearRowsLocked(view);

                viewers = views.Keys.ToList();
                views.Clear();
                DefaultItemsForSale.Clear();
            }

            foreach (var guid in viewers)
            {
                var viewer = ResolveViewer(guid);

                if (viewer?.CurrentSalvageRollerStation == this)
                    viewer.CurrentSalvageRollerStation = null;
            }
        }

        /// <summary>Destroys one view's displays, removes them from the registry, and empties the view's rows.</summary>
        private void ClearRowsLocked(View view)
        {
            foreach (var row in view.Rows)
            {
                DefaultItemsForSale.Remove(row.Display.Guid);

                if (!row.Display.IsDestroyed)
                    row.Display.Destroy();
            }

            view.Rows.Clear();
            view.SeqByDisplay.Clear();
            view.VisibleRows = new List<Row>();
        }

        /// <summary>
        /// Drops every view whose viewer is offline, out of range (<see cref="IsViewerOutOfRange"/>), or idle longer
        /// than <see cref="ViewLifetimeSeconds"/> at <paramref name="nowUnix"/>. The views are read under the lock, the
        /// viewers resolved outside it, so nothing else's lock is ever taken while this one is held. Returns the
        /// number dropped.
        /// </summary>
        internal int SweepViews(double nowUnix)
        {
            List<(uint Guid, View View, double LastTouched)> snapshot;

            lock (viewsLock)
            {
                if (views.Count == 0)
                    return 0;

                snapshot = views.Values.Select(v => (v.ViewerGuid, v, v.LastTouchedUnix)).ToList();
            }

            var lifetime = ViewLifetimeSeconds();
            var dropped = 0;

            foreach (var (guid, seen, lastTouched) in snapshot)
            {
                var viewer = ResolveViewer(guid);

                if (viewer != null && nowUnix - lastTouched <= lifetime && !IsViewerOutOfRange(viewer))
                    continue;

                // Re-checked and dropped in ONE lock section, so nothing can touch or refill the view between the
                // decision and the drop. A view replaced since the snapshot (a new View object), or touched since it
                // (a Use, an approach or a fill on another thread), is left for the next sweep.
                lock (viewsLock)
                {
                    if (!views.TryGetValue(guid, out var view) || !ReferenceEquals(view, seen) || view.LastTouchedUnix != lastTouched)
                        continue;

                    OnSweepDropping(guid);

                    ClearRowsLocked(view);
                    views.Remove(guid);
                }

                dropped++;
            }

            return dropped;
        }

        /// <summary>
        /// Called by the sweep after it has decided to drop a view and while it still holds the lock that made the
        /// decision, just before the drop. A seam for the tests: a touch attempted from another thread here must wait for
        /// the drop to finish, never land between the decision and the drop. Empty in production.
        /// </summary>
        protected virtual void OnSweepDropping(uint viewerGuid)
        {
        }

        /// <summary>The online player for a viewer guid, or null when they are offline. A seam for the tests.</summary>
        protected virtual Player ResolveViewer(uint viewerGuid) => PlayerManager.GetOnlinePlayer(viewerGuid);

        /// <summary>
        /// True when the viewer has left this station's landblock and its neighbours
        /// (<see cref="SalvageRollerStation.IsStale"/>; unknown on either side is in range). A seam for the tests.
        /// </summary>
        protected virtual bool IsViewerOutOfRange(Player viewer) => SalvageRollerStation.IsStale(CurrentLandblock, viewer.CurrentLandblock);

        /// <summary>The candidate sequence number behind a display guid, looked up in the viewer's own view only.</summary>
        internal bool TryGetRowSeq(uint viewerGuid, ObjectGuid displayGuid, out int seq)
        {
            lock (viewsLock)
            {
                if (views.TryGetValue(viewerGuid, out var view))
                    return view.SeqByDisplay.TryGetValue(displayGuid, out seq);
            }

            seq = 0;
            return false;
        }

        /// <summary>The display guids of every row the viewer's view holds, in row order. Empty with no view.</summary>
        internal List<ObjectGuid> DisplayGuidsOf(uint viewerGuid)
        {
            lock (viewsLock)
                return views.TryGetValue(viewerGuid, out var view) ? view.Rows.Select(r => r.Display.Guid).ToList() : new List<ObjectGuid>();
        }

        /// <summary>The guid of every viewer holding a view here.</summary>
        internal List<uint> ViewerGuids()
        {
            lock (viewsLock)
                return views.Keys.ToList();
        }

        /// <summary>Number of rows the viewer's view holds, whether or not the filter shows them. 0 with no view.</summary>
        internal int RowCount(uint viewerGuid)
        {
            lock (viewsLock)
                return views.TryGetValue(viewerGuid, out var view) ? view.Rows.Count : 0;
        }

        /// <summary>Number of rows the viewer's filter shows. 0 with no view.</summary>
        internal int VisibleCount(uint viewerGuid)
        {
            lock (viewsLock)
                return views.TryGetValue(viewerGuid, out var view) ? view.VisibleRows.Count : 0;
        }

        /// <summary>The viewer's live search filter, or null.</summary>
        internal Regex GetSearchFilter(uint viewerGuid)
        {
            lock (viewsLock)
                return views.TryGetValue(viewerGuid, out var view) ? view.SearchFilter : null;
        }

        /// <summary>
        /// Sets (or clears, with null) the viewer's display-only search filter, the same shape as
        /// PersonalVendor.SetSearchFilter. A row is shown when <see cref="ItemTextSearch.SearchText(WorldObject)"/>
        /// of its display object matches. Rows are never added, removed or destroyed by this. Another viewer's
        /// filter is never touched. Returns false when the viewer has no view.
        /// </summary>
        internal bool SetSearchFilter(uint viewerGuid, Regex filter)
        {
            lock (viewsLock)
            {
                if (!views.TryGetValue(viewerGuid, out var view))
                    return false;

                view.SearchFilter = filter;
                ApplyFilter(view);
                return true;
            }
        }

        private static void ApplyFilter(View view)
        {
            var filter = view.SearchFilter;

            view.VisibleRows = filter == null
                ? view.Rows.ToList()
                : view.Rows.Where(r => ItemTextSearch.IsMatch(filter, ItemTextSearch.SearchText(r.Display))).ToList();
        }

        /// <summary>The rows the viewer's filter shows, in row order. Nothing for a viewer with no view.</summary>
        public override void ForEachItemFor(Player viewer, Action<WorldObject> action)
        {
            foreach (var display in RowsFor(viewer))
                action(display);
        }

        /// <summary>Kept equal to what <see cref="ForEachItemFor"/> emits for the same viewer, which GameEventApproachVendor relies on: both read <see cref="RowsFor"/>.</summary>
        public override int GetItemsForSaleCount(Player viewer) => RowsFor(viewer).Count;

        /// <summary>Any row the viewer's own view holds, shown or not: the filter is display-only. Never another viewer's.</summary>
        public override bool TryGetItemForSale(Player viewer, ObjectGuid itemGuid, out WorldObject itemForSale)
        {
            itemForSale = null;

            if (viewer == null)
                return false;

            lock (viewsLock)
            {
                if (!views.TryGetValue(viewer.Guid.Full, out var view) || !view.SeqByDisplay.ContainsKey(itemGuid))
                    return false;

                return DefaultItemsForSale.TryGetValue(itemGuid, out itemForSale);
            }
        }

        /// <summary>Fails closed: with no viewer there is nobody whose rows could be shown.</summary>
        public override void forEachItem(Action<WorldObject> action)
        {
        }

        /// <summary>Fails closed, equal to what <see cref="forEachItem"/> emits.</summary>
        public override int ItemsForSaleCount => 0;

        /// <summary>Fails closed: with no viewer nothing resolves.</summary>
        public override bool TryGetItemForSale(ObjectGuid itemGuid, out WorldObject itemForSale)
        {
            itemForSale = null;
            return false;
        }

        #endregion

        #region Sell (target and Hammers in)

        /// <summary>
        /// True when the seller has a live view here, creating an EMPTY one first when theirs is gone but they are plainly
        /// at this station's panel: the last vendor or container they were sent (player.LastOpenedContainerId, set by
        /// Vendor.ApproachVendor) is this station, and they are not out of range (<see cref="IsViewerOutOfRange"/>). The
        /// idle sweep drops a view while the client's panel stays open (the client sends nothing when a vendor panel
        /// closes), and refusing that player's sale with <see cref="NoViewMessage"/> made them Use the station again for
        /// nothing.
        ///
        /// What a view made here can and cannot do: it is the seller's own, keyed by their guid, and starts with no rows,
        /// so it exposes nobody's rows and leaves DefaultItemsForSale untouched (the registry invariant holds). It skips
        /// the Use's reconcile, which is safe because the basket verify refuses a seller with any batch at all. It is marked
        /// UNFILLED until a Use or a fill: a take from it is refused with <see cref="NoViewMessage"/> (any row the client
        /// still shows is stale), and a sale refused because a batch exists is told <see cref="ResultsWaitingMessage"/>. A player
        /// who never opened this station, or opened something else since, or is out of range, is still refused. The
        /// player's slot moves here as a Use would move it, dropping a view held on another station. No lock is held
        /// while another is taken: OpenView and the other station's DropView each take only their own.
        /// </summary>
        internal bool TryEnsureSellerView(Player player)
        {
            if (player == null)
                return false;

            if (HasView(player))
                return true;

            if (IsDestroyed || player.LastOpenedContainerId != Guid || IsViewerOutOfRange(player))
                return false;

            var previous = player.CurrentSalvageRollerStation;

            if (previous != null && !ReferenceEquals(previous, this))
                previous.DropView(player);

            OpenUnfilledView(player.Guid.Full);

            player.CurrentSalvageRollerStation = this;

            return true;
        }

        /// <summary>Per-item preflight: a player with a live view (<see cref="TryEnsureSellerView"/>) only. The basket rules live in <see cref="TryValidateSellBasket"/>.</summary>
        public override bool CanAccept(WorldObject wo, Player player, out string reason)
        {
            if (!TryEnsureSellerView(player))
            {
                reason = NoViewMessage;
                return false;
            }

            reason = null;
            return true;
        }

        /// <summary>
        /// The whole-basket gate (DESIGN 5.3, 5.4 step 3), run before anything is detached. Refuses a player with
        /// no live view (<see cref="TryEnsureSellerView"/>), refuses when any requested line was dropped on the way here (requested count must equal
        /// accepted count), then hands the accepted basket to SalvageRollerEligibility.VerifyBasket through
        /// <see cref="BasketVerifier"/>.
        /// </summary>
        public override bool TryValidateSellBasket(Player player, IReadOnlyList<WorldObject> requested, IReadOnlyList<WorldObject> accepted, out string reason)
        {
            if (!TryEnsureSellerView(player))
            {
                reason = NoViewMessage;
                return false;
            }

            if (requested == null || accepted == null || accepted.Count == 0 || requested.Count != accepted.Count)
            {
                reason = BasketLineDroppedMessage;
                return false;
            }

            var verifier = BasketVerifier;

            if (verifier == null)
            {
                reason = BasketUnavailableMessage;
                return false;
            }

            if (verifier(player, accepted, requested.Count, out reason))
                return true;

            // A seller on an unfilled view who already has a batch cannot see its results here, so "take one of its
            // results first" would be a dead end: point them at the Use that shows them.
            if (reason == SalvageRollerEligibility.BatchExistsMessage && IsUnfilledView(player.Guid.Full))
                reason = ResultsWaitingMessage;

            return false;
        }

        private static bool DefaultBasketVerifier(Player player, IReadOnlyList<WorldObject> offered, int requestedCount, out string refusal)
        {
            return SalvageRollerEligibility.VerifyBasket(player, offered, requestedCount, out _, out _, out _, out refusal);
        }

        /// <summary>
        /// The escrow pipeline (DESIGN 5.4 step 4), run by SalvageRollerManager.Sell on the items the sell path has
        /// detached and saved: the post-detach backstop, the batch rows and audit rows, escrow, then the forced-pick
        /// confirmation. Reached only when <see cref="TryValidateSellBasket"/> accepted the basket.
        ///
        /// Before any batch row exists the only safe home for a detached item is the seller's pack, so every refusal on
        /// that side comes back through <see cref="HandBack"/>, one item at a time. With no seller (never the case in
        /// production) there is nobody to open a batch for, and every item is handed back the same way.
        /// </summary>
        public override void ProcessItemsForPurchase(Player player, Dictionary<uint, WorldObject> items)
        {
            var detached = items?.Values.Where(i => i != null).ToList() ?? new List<WorldObject>();

            var handled = false;

            if (player != null && detached.Count > 0)
            {
                try
                {
                    SalvageRollerManager.Sell(player, this, detached, item => HandBack(item, player));
                    handled = true;
                }
                catch (Exception ex)
                {
                    // The pipeline threw part way. Whatever is still unparented goes home now; anything a batch row
                    // covers and that is in escrow stays there for reconcile (HandBack skips a parented item).
                    SalvageRollerManager.Log.Error($"{SalvageRollerManager.RefundPrefix} {Name}: the sell pipeline threw for {player.Name} (0x{player.Guid.Full:X8}); handing back every unparented item.", ex);
                }
            }

            if (!handled)
                HandBackEach(detached, player);

            if (player?.Session == null)
                return;

            var panelScope = SellPhaseProfile.Begin();

            ApproachVendor(player, VendorType.Sell);

            SellPhaseProfile.End(SellPhase.Panel, panelScope);
        }

        /// <summary>
        /// Hands back each item on its own: one failure (a throw included) must never skip the rest, because every item
        /// here is already detached and the hand-back is its only way home. The seller is told about a throw too, and
        /// a throwing tell cannot stop the remaining hand-backs either.
        /// </summary>
        private void HandBackEach(List<WorldObject> detached, Player player)
        {
            foreach (var item in detached)
            {
                try
                {
                    HandBack(item, player);
                }
                catch (Exception ex)
                {
                    SalvageRollerManager.Log.Error($"{SalvageRollerManager.RefundPrefix} ORPHAN: 0x{item?.Guid.Full ?? 0:X8} (wcid {item?.WeenieClassId ?? 0}, {item?.Name}) threw while being returned to {(player?.Name ?? "<no player>")} after a sale to the Salvage Roller. It is detached and needs manual recovery.", ex);
                    SafeTellHandBackFailed(player);
                }
            }
        }

        /// <summary>
        /// The mule vendor's HandBackOrphan rule: an item still parented somewhere is already home and is left
        /// alone; anything else goes back into the seller's pack and is saved there. A failed placement is logged
        /// as an ORPHAN for manual recovery. A placement that worked but whose SAVE threw is a different fact - the
        /// item is in the pack in memory and the character's next save records it - and gets its own line and log.
        /// </summary>
        private void HandBack(WorldObject item, Player player)
        {
            if (item == null)
                return;

            if (IsAlreadyParented(item))
            {
                log.Warn($"[SALVAGE ROLLER] {Name}: 0x{item.Guid.Full:X8} ({item.Name}) is still parented (container 0x{item.ContainerId:X8}, wielder 0x{item.WielderId:X8}); not handing it back.");
                return;
            }

            if (TryHandBack(item, player))
            {
                try
                {
                    item.SaveBiotaToDatabase();
                }
                catch (Exception ex)
                {
                    log.Error($"[SALVAGE ROLLER] UNSAVED: 0x{item.Guid.Full:X8} (wcid {item.WeenieClassId}, {item.Name}) is back in {(player?.Name ?? "<no player>")}'s pack, but saving it there threw. It is NOT detached; the character's next save records it.", ex);
                    SafeTellHandBackUnsaved(player);
                }

                return;
            }

            SalvageRollerManager.Log.Error($"{SalvageRollerManager.RefundPrefix} ORPHAN: 0x{item.Guid.Full:X8} (wcid {item.WeenieClassId}, {item.Name}) could not be returned to {(player?.Name ?? "<no player>")} after a sale to the Salvage Roller. It is detached and needs manual recovery.");
            SafeTellHandBackFailed(player);
        }

        /// <summary><see cref="TellHandBackFailed"/>, never throwing: a failed tell must not abort the remaining hand-backs.</summary>
        private void SafeTellHandBackFailed(Player player)
        {
            try
            {
                TellHandBackFailed(player);
            }
            catch (Exception ex)
            {
                log.Error($"[SALVAGE ROLLER] {Name}: telling {(player?.Name ?? "<no player>")} about a failed hand-back threw.", ex);
            }
        }

        /// <summary><see cref="TellHandBackUnsaved"/>, never throwing.</summary>
        private void SafeTellHandBackUnsaved(Player player)
        {
            try
            {
                TellHandBackUnsaved(player);
            }
            catch (Exception ex)
            {
                log.Error($"[SALVAGE ROLLER] {Name}: telling {(player?.Name ?? "<no player>")} about an unsaved hand-back threw.", ex);
            }
        }

        /// <summary>The seller-facing half of an unsaved hand-back. A seam, like <see cref="TellHandBackFailed"/>.</summary>
        protected virtual void TellHandBackUnsaved(Player player)
        {
            Tell(player, HandBackUnsavedMessage);
        }

        /// <summary>
        /// The seller-facing half of a failed hand-back, as the mule vendor's HandBackOrphan sends one. A seam so a
        /// test can see it ran without a live Session; production sends a transient error when there is a session.
        /// </summary>
        protected virtual void TellHandBackFailed(Player player)
        {
            Tell(player, HandBackFailedMessage);
        }

        private static bool IsAlreadyParented(WorldObject item)
        {
            return item != null && (item.ContainerId != null || item.WielderId != null);
        }

        /// <summary>
        /// The placement attempt, as its own seam (the mule vendor's TryHandBack pattern) so a test can see the
        /// hand-back genuinely ran. Re-checks the parented guard because it is the seam.
        /// </summary>
        protected virtual bool TryHandBack(WorldObject item, Player player)
        {
            if (IsAlreadyParented(item))
                return false;

            // the client may already hold this guid; the pack create forgets its effect record (see
            // PlayerSalvageRollerHost.TryPutInPack)
            return player != null && item != null && player.TryCreateInInventoryWithNetworking(item);
        }

        #endregion

        #region Buy (take one candidate)

        /// <summary>
        /// The take (DESIGN 5.4 step 7, item 1 plus the view half of item 2): validated by
        /// <see cref="TryResolveTake"/>, then handed to <see cref="TakeHandler"/> with the chosen sequence
        /// number. A refusal is told to the player and returns false, and Player_Commerce re-sends the panel.
        /// </summary>
        public override bool BuyItems_ValidateTransaction(List<ItemProfile> itemProfiles, Player player)
        {
            if (!TryResolveTake(player, itemProfiles, out var seq, out var refusal))
            {
                Tell(player, refusal);
                return false;
            }

            var handler = TakeHandler;

            return handler != null && handler(player, this, seq);
        }

        /// <summary>
        /// The testable core of the take's validation: the buyer has a live view, the buy is exactly one profile
        /// with an amount of one, and that profile names a row the BUYER'S OWN view holds (shown or filtered out).
        /// Another viewer's display guid never resolves, even when the buyer's own batch has the same sequence number:
        /// a global guid-to-seq map would silently hand the buyer their own #k.
        /// </summary>
        internal bool TryResolveTake(Player player, List<ItemProfile> itemProfiles, out int seq, out string refusal)
        {
            seq = 0;

            // An unfilled view was made by a sale and never shown this batch's rows, so any row the client offers is stale:
            // a Use is what shows the current ones.
            if (!HasView(player) || IsUnfilledView(player.Guid.Full))
            {
                refusal = NoViewMessage;
                return false;
            }

            if (itemProfiles == null || itemProfiles.Count != 1 || itemProfiles[0] == null || itemProfiles[0].Amount != 1)
            {
                refusal = TakeExactlyOneMessage;
                return false;
            }

            if (!TryGetRowSeq(player.Guid.Full, new ObjectGuid(itemProfiles[0].ObjectGuid), out seq))
            {
                refusal = UnknownRowMessage;
                return false;
            }

            refusal = null;
            return true;
        }

        /// <summary>The take state machine (DESIGN 5.4 step 7): SalvageRollerManager.TakeFromStation.</summary>
        private static bool DefaultTakeHandler(Player player, SalvageRollerVendor station, int seq)
        {
            return SalvageRollerManager.TakeFromStation(player, station, seq);
        }

        #endregion

        /// <summary>A transient error to <paramref name="player"/>, skipped when there is no session to send it on.</summary>
        private static void Tell(Player player, string text)
        {
            if (player?.Session == null || string.IsNullOrEmpty(text))
                return;

            player.SendTransientError(text);
        }
    }
}
