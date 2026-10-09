using System;
using System.Collections.Generic;

using log4net;

using System.Linq;
using System.Text.RegularExpressions;

using ACE.Database;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Entity.AccountVault;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Managers.Market;
using ACE.Server.Network.GameMessages;
using ACE.Server.Network.GameMessages.Messages;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// Mule Vendor (Docs/MuleVendor/DESIGN.md sections 5, 9, 9.1, 10, 12; risk R10): a disposable
    /// WINDOW onto one account's <see cref="AccountVaultStore"/>, presented through the ordinary
    /// vendor buy/sell panel. Selling deposits, buying withdraws, every price is 0. Several
    /// PersonalVendor windows may exist over the SAME store at once (a share grantee summoning their
    /// own copy), and none of them holds authoritative state - <see cref="Store"/> does, per DESIGN
    /// section 5.
    ///
    /// THE INVARIANT THIS CLASS EXISTS TO PROTECT: WorldObject.Destroy's vendor branch
    /// (WorldObject.cs:921-927) destroys every entry in BOTH base Vendor dictionaries
    /// (DefaultItemsForSale, UniqueItemsForSale), and a mule is destroyed routinely - logout,
    /// re-summon, rot, the distance leash. A STORED item must therefore never enter either
    /// dictionary. The split that makes this safe by construction, not by a guard someone can delete:
    ///
    ///  - DefaultItemsForSale holds ONLY disposable display objects that materialize a collapsed
    ///    ledger row (account_vault_stack). They are not authoritative: each
    ///    <see cref="RebuildView"/> keeps an unchanged row's object and replaces a changed one, so
    ///    Destroy() reaching them is correct - it is exactly retail's own "buying a default item
    ///    creates a fresh one" semantic (DESIGN 9.1).
    ///  - UniqueItemsForSale stays permanently EMPTY here. ProcessItemsForPurchase is overridden
    ///    precisely so nothing is ever put there.
    ///  - Real stored biotas - the actual withdrawable items, still living inside the store's own
    ///    vault Container objects - are referenced from <see cref="storeItems"/>, a THIRD dictionary
    ///    private to this class, which WorldObject.Destroy does not know about and cannot reach.
    ///
    /// <see cref="AddDefaultItem"/> is additionally overridden as a redundant, LOUD guard: it refuses
    /// (and logs an error) if the item being added is one of the store's own biotas. The dictionary
    /// split above is the real mitigation; this guard exists only to make a future mistake noisy
    /// rather than silent.
    /// </summary>
    public partial class PersonalVendor : Vendor
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// The account whose store this window looks onto. Computed from <see cref="Store"/> rather
        /// than tracked separately - DESIGN section 5 says the window holds no authoritative state,
        /// and a second field here would just be a second place for it to disagree with the store it
        /// is supposedly describing. 0 (no real account ever has this id) when no store is bound.
        /// </summary>
        public uint StoreAccountId => store?.AccountId ?? 0;

        /// <summary>The character who summoned this window. Informational (naming, logging); not an authorization input - see <see cref="TryAuthorize"/>.</summary>
        public uint SummonerGuid { get; set; }

        private AccountVaultStore store;

        /// <summary>
        /// The store this window looks onto. Settable - not a get-only property computed on demand via
        /// AccountVaultManager.GetStore - because something outside this class has to be able to bind a
        /// specific store instance to this window: in production, the /mule summon path (a later task);
        /// in tests, a fake-backed AccountVaultStore built the same way Task 4's own tests build one
        /// (new AccountVaultStore(accountId, fakeBackend, fakeWorld)). A get-only computed property
        /// would be unreachable from a unit test with no live shard database and would leave the summon
        /// path nothing to attach.
        ///
        /// `internal set` - the test assembly keeps write access through the existing
        /// InternalsVisibleTo, but nothing outside ACE.Server should be reassigning a live vendor's
        /// store.
        ///
        /// The setter is where this window's ownership of its own window accounting lives (DESIGN
        /// section 5's ruling: the vendor is the window and knows its own lifetime, not the summon
        /// task): binding a new store calls <see cref="AccountVaultStore.AddWindow"/> so the idle sweep
        /// cannot retire a store a live vendor is still looking at, and swapping away from a previous
        /// store calls <see cref="AccountVaultStore.RemoveWindow"/> on it first.
        ///
        /// Three cases:
        ///  - Same instance rebound (or null-to-null): no-op. Avoids double-counting a window on a
        ///    redundant assignment.
        ///  - A DIFFERENT account's store: REFUSED and logged. A window must never be silently
        ///    re-pointed at another account's vault - that would leave the previous store's biotas
        ///    addressable through TryGetItemForSale.
        ///  - A different INSTANCE for the SAME account (the case I8's store re-fetch after an idle
        ///    eviction produces): allowed. storeItems is cleared either way on a real rebind, because a
        ///    stale reference into the OLD store's containers must never linger reachable through this
        ///    window once the window points elsewhere.
        /// </summary>
        public AccountVaultStore Store
        {
            get => store;
            internal set
            {
                if (ReferenceEquals(store, value))
                    return;

                if (value != null && store != null && store.AccountId != value.AccountId)
                {
                    log.Error($"[MULE VENDOR] {Name}: refused to rebind Store from account {store.AccountId} to account {value.AccountId} - a window must never be re-pointed at a different account's vault.");
                    return;
                }

                store?.RemoveWindow();

                store = value;
                storeItems.Clear();
                sortNames.Clear();

                // AFTER the account-mismatch refusal above, not before it: a refused rebind leaves the
                // OLD store in place, and resetting there would throw away a cache entry that is still
                // correct. -1 can never equal a real version (they start at 0 and only rise), so the
                // next RebuildView always rebuilds against whatever store this window now points at.
                builtFromVersion = -1;

                // Same reasoning as storeItems: every value in here is a live reference into the OLD
                // store's vault containers, and a stale one must not stay reachable through this
                // window once it points somewhere else. The proxy display objects themselves are left
                // for the next RebuildView to keep or destroy, exactly as the ledger displays beside
                // them are - a kept one is re-bound to the NEW store's members there, never to these.
                proxiedItems.Clear();

                // Same reason again: a class row resolved against the OLD store must not stay
                // reachable through a window that now points at another account's vault.
                classDisplays.Clear();

                // And the display records, because DisplayKey is not account-scoped: a window
                // rebound from one store to another (A -> null -> B) must never let B's
                // same-wcid, same-count row claim a display object built for A. The objects
                // themselves stay in DefaultItemsForSale; with no records nothing is claimed, so
                // the next RebuildView's finally destroys every one of them.
                displayRecords.Clear();

                // And any deferred panel refresh: it was owed for the OLD binding. WorldObject.Destroy
                // sets Store = null, so this is also where a destroyed window drops its pending ones.
                ClearPendingRefreshes();

                store?.AddWindow();
            }
        }

        /// <summary>
        /// Live references into the store's own vault Container objects - the actual withdrawable
        /// biotas, as opposed to <see cref="Vendor.DefaultItemsForSale"/>'s disposable ledger display
        /// objects. WorldObject.Destroy's vendor branch only walks DefaultItemsForSale and
        /// UniqueItemsForSale (WorldObject.cs:921-927), so this dictionary is what keeps a stored item
        /// outside its reach. Rebuilt on every <see cref="RebuildView"/> call; items in here are never
        /// destroyed by this class.
        /// </summary>
        private readonly Dictionary<ObjectGuid, WorldObject> storeItems = new Dictionary<ObjectGuid, WorldObject>();

        /// <summary>
        /// The <see cref="AccountVaultStore.Version"/> the current contents of <see cref="storeItems"/>
        /// and DefaultItemsForSale were built from, or -1 for "nothing valid is built". See
        /// <see cref="RebuildView"/> for why -1 can never collide with a real version.
        /// </summary>
        private long builtFromVersion = -1;

        /// <summary>
        /// Code review (independent audit on origin/master...HEAD): whether the build stamped by
        /// <see cref="builtFromVersion"/> ran while the store reported IsLoaded true. The count clause
        /// below (DefaultItemsForSale.Count + storeItems.Count > 0) exists ONLY to catch the transient
        /// cold-load case - AccountVaultStore.GetEntries returns an empty list, never a partial one,
        /// until every one of the account's vaults finishes its async load, and finishing that load
        /// bumps no version - so a store that was still loading on the first approach must keep retrying
        /// on every subsequent one rather than caching an empty view forever.
        ///
        /// But a search filter that matches NOTHING against an already-loaded, non-empty store produces
        /// that exact same "materialized nothing" shape for a completely different reason, and the count
        /// clause could not tell the two apart: with the count staying 0 for as long as that filter
        /// stood, every approach re-ran the full GetEntries loop (a GetCachedWeenie call plus a regex
        /// match per ledger row) instead of taking the cache hit the version match should have earned
        /// it. Stamped alongside builtFromVersion, this lets the cheap-exit tell "still loading" (must
        /// keep retrying) apart from "loaded, and genuinely nothing matched" (safe to cache) without
        /// changing the cold-load behavior at all.
        /// </summary>
        private bool builtFromLoadedStore;

        /// <summary>
        /// /mule search's live filter (display-side only - never touches the store, ledger, or deposit
        /// path). Null means "no filter, show everything". Set via <see cref="SetSearchFilter"/>, which
        /// also forces the next <see cref="RebuildView"/> to actually run rather than short-circuiting
        /// on the cheap-exit check.
        /// </summary>
        internal Regex SearchFilter { get; private set; }

        /// <summary>Entries actually materialized into the view by the last <see cref="RebuildView"/> call.</summary>
        internal int LastRebuildShown { get; private set; }

        /// <summary>Entries considered (before filtering) by the last <see cref="RebuildView"/> call.</summary>
        internal int LastRebuildTotal { get; private set; }

        /// <summary>Display objects the last <see cref="RebuildView"/> that actually ran had to materialize fresh.</summary>
        internal int LastRebuildCreated { get; private set; }

        /// <summary>Display objects the last <see cref="RebuildView"/> that actually ran carried over unchanged from the build before it.</summary>
        internal int LastRebuildReused { get; private set; }

        /// <summary>Display objects the last <see cref="RebuildView"/> that actually ran destroyed because nothing in the new view claimed them.</summary>
        internal int LastRebuildDestroyed { get; private set; }

        /// <summary>
        /// Which kind of row a <see cref="DisplayKey"/> names. Part of the key, not just a label: a
        /// ledger row and a class row can share a wcid while being different holdings (see
        /// <see cref="classDisplays"/>), so a key without its kind could hand one row's display object
        /// to the other.
        /// </summary>
        private enum DisplayKind
        {
            Ledger,
            Class,
            ProxyOne,
            ProxyGroup,
        }

        /// <summary>
        /// The identity of one display row across rebuilds: which kind of row it is plus the id that
        /// kind is stable on - a ledger row's wcid, a class line's ClassDisplayId, a singleton proxy's
        /// stored guid, a group proxy's representative guid.
        /// </summary>
        private readonly record struct DisplayKey(DisplayKind Kind, uint Id, string Text);

        /// <summary>
        /// Everything a display row's RENDERED state is derived from, read off the VaultEntry alone
        /// (no materialization). Two passes that compute equal signatures for the same key would build
        /// indistinguishable display objects, which is what makes it safe to keep the first one. Fields
        /// a kind does not use stay at their defaults. Primitives only - never a reference to a stored
        /// biota (see <see cref="displayRecords"/>).
        /// </summary>
        private sealed record DisplaySig(
            uint Wcid,
            long Count,
            long TotalValue = 0,
            string CanonicalForm = null,
            int? StackSize = null,
            string Name = null,
            int? Value = null,
            int? MaterialType = null,
            int? ItemWorkmanship = null,
            int? NumItemsInMaterial = null,
            int? Structure = null,
            int? MaxStructure = null,
            int ItemType = 0);

        /// <summary>
        /// One display object this window built, the signature it was built from, and the pre-suffix
        /// Name it sorts on (null when no " (N in vault)" suffix was applied).
        /// </summary>
        private sealed record DisplayRecord(WorldObject Display, DisplaySig Sig, string BaseName);

        /// <summary>
        /// The display objects the last <see cref="RebuildView"/> built or kept, keyed by row identity,
        /// so the next rebuild can KEEP an unchanged row's object (and therefore its guid) instead of
        /// destroying and re-materializing it. A client plugin re-identifies every guid it has not seen
        /// before, so recreating up to a whole vault's worth of rows after every single withdraw or
        /// deposit was a visible lag spike for what is usually a one-row change.
        ///
        /// Holds ONLY disposable display objects and primitives. The lookup maps that turn a display
        /// guid back into something withdrawable (<see cref="storeItems"/>, <see cref="proxiedItems"/>,
        /// <see cref="classDisplays"/>) are never carried over: every rebuild refills them from the
        /// current pass's entries, so a reused guid always resolves to the CURRENT members of its row.
        /// </summary>
        private Dictionary<DisplayKey, DisplayRecord> displayRecords = new Dictionary<DisplayKey, DisplayRecord>();

        /// <summary>
        /// Sets (or clears, with null) the live search filter and invalidates the cached view so the
        /// next <see cref="RebuildView"/> call rebuilds against it instead of short-circuiting on the
        /// "nothing changed" cheap-exit - see that method's remarks for why -1 is what forces a rebuild.
        /// </summary>
        internal void SetSearchFilter(Regex filter)
        {
            SearchFilter = filter;
            builtFromVersion = -1;
        }

        /// <summary>
        /// Display proxy guid -> the real stored biotas it stands in for.
        ///
        /// Usually exactly one. It is a LIST because a row may be a GROUP of equivalent stored biotas
        /// collapsed for display by the 2026-08-31 grouping design - hundreds of salvage bags reading
        /// the same material, units and workmanship present as one line, and the withdraw path needs
        /// the whole membership to hand over k of them. The first element is always the representative
        /// the proxy was built from.
        ///
        /// The client's vendor panel files every row under a category derived from the row's
        /// <see cref="ItemType"/>, and it has no category for some of them - see
        /// <see cref="PanelRenderableItemTypes"/>. A row of such a type is silently dropped by the client:
        /// the server sends it, nothing errors, and the panel simply never draws it. A stored item of
        /// that type is therefore invisible and unwithdrawable even though the vault holds it happily.
        ///
        /// So a stored item of a hidden type is presented through a DISPOSABLE display object stamped
        /// with a renderable ItemType, exactly as a collapsed ledger row already is, and this map is
        /// how a buy request against that proxy's guid finds the real biota again. The stored item
        /// itself is never touched - its ItemType is what every custom crafting intercept matches on
        /// (EquipmentModManager.IsModMaterial and the three matchers dispatched beside it in
        /// RecipeManager.UseObjectOnTarget), so changing it on the real object would quietly break
        /// crafting for that item forever.
        ///
        /// A proxied item is deliberately NOT also placed in <see cref="storeItems"/>: it is
        /// represented by exactly one row, the proxy. The proxies themselves live in
        /// DefaultItemsForSale alongside the ledger displays, which is what makes them counted by
        /// <see cref="ItemsForSaleCount"/>, serialized by <see cref="forEachItem"/>, kept or replaced by
        /// the next <see cref="RebuildView"/>, and - the part that matters for safety - resolved by
        /// <see cref="TryGetItemForSale"/> so that Player_Inventory's `rootOwner is Vendor` guard
        /// still refuses any attempt to wield or move one directly out of the panel.
        /// </summary>
        private readonly Dictionary<ObjectGuid, IReadOnlyList<WorldObject>> proxiedItems = new Dictionary<ObjectGuid, IReadOnlyList<WorldObject>>();

        /// <summary>
        /// Counted item-CLASS rows, keyed by the guid of the disposable display object that stands for
        /// one. The value is the row itself, so a buy request can be turned back into a class withdraw.
        ///
        /// THIS MAP IS A SAFETY REQUIREMENT, not a convenience. A class display lives in
        /// DefaultItemsForSale beside the ledger displays, and the withdraw resolver's last arm reads
        /// anything it finds there as a LEDGER row of that display's wcid. A class row and a ledger row
        /// of the same wcid are different holdings - a bag of 73 salvage and a pile of pristine full
        /// bags - so without this map a player buying a class row would silently debit the ledger and
        /// be handed the wrong item, or be refused while the row they clicked sat untouched. It is
        /// therefore consulted BEFORE that arm, exactly as <see cref="proxiedItems"/> is.
        ///
        /// Cleared wherever <see cref="proxiedItems"/> is, and for the same reason: an entry must never
        /// outlive the display guid it was keyed to.
        /// </summary>
        private readonly Dictionary<ObjectGuid, VaultEntry> classDisplays = new Dictionary<ObjectGuid, VaultEntry>();

        /// <summary>
        /// The display Name a row had BEFORE a " (N in vault)" count suffix was appended to it,
        /// keyed by the row's display guid. forEachItem's Name rung reads through this map instead
        /// of Name itself whenever an entry exists, so the count - which changes on every
        /// withdrawal and would otherwise reorder same-name rows as N crosses an ordinal string
        /// boundary (e.g. "(3 in vault)" sorting before "(12 in vault)", or the suffix disappearing
        /// entirely once N drops to 1) - never influences sort order.
        ///
        /// Populated by <see cref="RebuildView"/> for every row whose display carries the suffix -
        /// ledger rows, class rows and group proxies - from the base name its build captured. A row
        /// KEPT from the previous build (same guid) has its recorded base name re-registered here, so
        /// clearing this map does not lose it.
        /// Cleared everywhere <see cref="DefaultItemsForSale"/>/<see cref="storeItems"/> are cleared
        /// (the Store setter and RebuildView), so an entry can never outlive the guid it was keyed
        /// to - a fresh display object with the same table slot but a reused or new guid starts with
        /// no entry here and falls back to reading Name directly.
        /// </summary>
        private readonly Dictionary<ObjectGuid, string> sortNames = new Dictionary<ObjectGuid, string>();

        /// <summary>
        /// The ItemTypes the client's vendor panel is KNOWN to file under a category tab, because
        /// retail vendors stock items of each and players have always seen them.
        ///
        /// Expressed as the renderable set rather than as the hidden one deliberately. ItemType is a
        /// [Flags] enum, and while nearly every weenie carries exactly one bit, combinations do occur -
        /// ace_world holds two weenies at 63 (MeleeWeapon|Armor|Clothing|Jewelry|Creature|Food). A
        /// "does it carry a hidden bit" test proxies those, because Creature is in the hidden set,
        /// even though the panel can perfectly well file them as a melee weapon. Asking instead
        /// whether ANY renderable bit is present gets the combination right in the only direction that
        /// matters: if the client has a tab it can use, leave the item alone.
        ///
        /// This list is the 23 distinct ItemTypes actually stocked by vendor create lists across all
        /// of ace_world, counted 2026-08-28. Everything outside it is treated as hidden:
        /// TinkeringMaterial (confirmed in play, see <see cref="NeedsDisplayProxy"/>), plus Creature,
        /// Portal, Lockable, MagicWieldable, CraftFletchingBase, LifeStone and Gameboard, which are
        /// unconfirmed but were swept in on the repo owner's ruling to proxy them for safety rather
        /// than wait for each to be caught in play. ItemType.None (68 weenies carry it) falls outside
        /// too, which is correct: no bits means no tab either.
        ///
        /// The cost of a wrongly-listed-as-hidden type is bounded and cosmetic - that item's vendor
        /// row shows the handful of properties TryCreateDisplayProxy copies rather than its full
        /// appraisal, and the real item is untouched and withdraws intact. The cost of a wrongly
        /// omitted one is an item nobody can get back out of their vault. The asymmetry is why the
        /// unconfirmed seven are in.
        /// </summary>
        private const ItemType PanelRenderableItemTypes =
            ItemType.MeleeWeapon | ItemType.Armor | ItemType.Clothing | ItemType.Jewelry |
            ItemType.Food | ItemType.Money | ItemType.Misc | ItemType.MissileWeapon |
            ItemType.Container | ItemType.Useless | ItemType.Gem | ItemType.SpellComponents |
            ItemType.Writable | ItemType.Key | ItemType.Caster | ItemType.PromissoryNote |
            ItemType.ManaStone | ItemType.Service | ItemType.CraftCookingBase |
            ItemType.CraftAlchemyBase | ItemType.CraftAlchemyIntermediate |
            ItemType.CraftFletchingIntermediate | ItemType.TinkeringTool;

        /// <summary>
        /// The ItemType a proxy row is stamped with. Misc is the category the Sealed hammers already
        /// render under, so it is the one choice observed to work for exactly this family of items.
        /// </summary>
        private const ItemType ProxyDisplayItemType = ItemType.Misc;

        /// <summary>
        /// True when the panel has no category tab it could file this ItemType under, so a stored item
        /// carrying it needs a display proxy to be visible at all.
        ///
        /// The one confirmed case is <see cref="ItemType.TinkeringMaterial"/>, found in play
        /// 2026-08-28: unsealed salvage and every unsealed custom mod hammer (wcids 1001910-1001918)
        /// deposited into a vault successfully, were logged as deposited, and then appeared nowhere in
        /// the panel - while the Sealed hammers beside them in the same vault (1001913/1001915/1001917,
        /// ItemType.Misc) rendered normally under "Miscellaneous". The client is told ItemType and
        /// never WeenieType (WorldObject_Networking.cs:80 writes the former; the latter is only read
        /// there to derive openable/sentinel flags), so ItemType is the only input the panel could be
        /// filing on. The rest of the hidden set is unconfirmed and included for safety - see
        /// <see cref="PanelRenderableItemTypes"/> for that decision and its cost.
        ///
        /// <see cref="WorldObject.ItemType"/> is NON-nullable (it reads
        /// `(ItemType)(GetProperty(PropertyInt.ItemType) ?? 0)`), so a real item never arrives here as
        /// null - an unset ItemType arrives as None, which correctly needs a proxy because no bits
        /// means no tab. The nullable parameter exists only so a caller holding a nullable does not
        /// have to unwrap it.
        ///
        /// Internal so the test project can exercise the rule directly without building a vendor.
        /// </summary>
        internal static bool NeedsDisplayProxy(ItemType? itemType)
        {
            return ((itemType ?? ItemType.None) & PanelRenderableItemTypes) == 0;
        }

        /// <summary>
        /// A new biota be created taking all of its values from weenie.
        /// </summary>
        public PersonalVendor(Weenie weenie, ObjectGuid guid) : base(weenie, guid)
        {
            SetEphemeralValues();
        }

        /// <summary>
        /// Restore a WorldObject from the database.
        ///
        /// Reachable only if DESIGN 11.5's shard-persistence exclusion is ever bypassed (a bug, not a
        /// supported path - see WorldObjectFactory's biota arm and its comment). Implemented anyway:
        /// silently downgrading a persisted mule to a plain Vendor here would hand a store's contents
        /// to the retail rot-and-destroy path.
        /// </summary>
        public PersonalVendor(Biota biota) : base(biota)
        {
            SetEphemeralValues();
        }

        /// <summary>
        /// DESIGN section 12 (anti-grief), all deliberate. With summoning permitted anywhere - a
        /// dungeon corridor, a doorway, a boss room - these two are the only thing standing between
        /// this feature and a body-blocking, aggro-soaking grief tool. Neither may be relaxed later as
        /// an optimization.
        /// </summary>
        private void SetEphemeralValues()
        {
            // Ethereal: copied from Pet.SetEphemeralValues (Pet.cs:55). Side benefit:
            // IsProjectileVisible short-circuits to true for an ethereal creature (WorldObject.cs:436),
            // so a mule cannot be used as a projectile shield. Do NOT copy the rest of
            // Pet.SetEphemeralValues: it also sets RadarBehavior.ShowNever and ItemUseable.No, and a
            // mule needs the opposite of both.
            Ethereal = true;

            // Vendor descends from Creature, so an unpinned mule is a targetable creature that can
            // soak aggro or body-block. Asserted here AND set in the weenie, in two places, so a bad
            // content edit cannot reintroduce it.
            Attackable = false;
        }

        #region Required overrides (DESIGN 9.1)

        /// <summary>
        /// Retail builds DefaultItemsForSale once from the weenie's create list. A mule's stock comes
        /// from the store instead, rebuilt per approach by <see cref="RebuildView"/> - so there is
        /// nothing for this to do, and it must do nothing: the base implementation would happily
        /// populate DefaultItemsForSale from a create list this weenie is not expected to carry.
        /// </summary>
        protected override void LoadInventory()
        {
        }

        /// <summary>
        /// Display-only ordering over the disposable view (see class remarks): storeItems and
        /// DefaultItemsForSale are concatenated and re-sorted here, rather than emitted as two
        /// separate runs the way the base implementation does it. AccountVaultStore.GetEntries
        /// documents its own order as "Deterministic, not stable" across restarts
        /// (AccountVaultStore.cs:1007-1015), so without this the panel's row order would shuffle
        /// from one restart to the next, and real stored biotas would always arrive as one block
        /// ahead of collapsed ledger rows regardless of what a player would expect to see next to
        /// each other.
        ///
        /// The composite key itself lives in <see cref="VaultDisplaySort"/> - see that type's doc
        /// comment for all nine rungs, what each null means, and why each rung is there. It was moved
        /// out of this method on 2026-09-21 so solo Thread Cache contents could be ordered by the SAME
        /// key (user ruling: "solo caches get the FULL composite /mule sort") without a second copy of
        /// it, which would have drifted the first time either side was tuned. Nothing about the order
        /// changed in that move.
        ///
        /// The one caller-specific part stays here: rung 6 (Name) sorts through <see cref="sortNames"/>
        /// when the row has an entry, so a row whose displayed Name carries a " (N in vault)" count
        /// suffix sorts on its BASE name. That map is a mule concept; VaultDisplaySort takes it as a
        /// resolver and every other caller passes none.
        ///
        /// storeItems' real biotas are only READ here, never mutated - this changes the order
        /// forEachItem visits them in and nothing else. Base Vendor.forEachItem is untouched: a
        /// retail vendor's create-list order is deliberate and must stay exactly as it is.
        /// </summary>
        public override void forEachItem(Action<WorldObject> action)
        {
            var ordered = VaultDisplaySort.Order(
                storeItems.Values.Concat(DefaultItemsForSale.Values),
                guid => sortNames.TryGetValue(guid, out var baseName) ? baseName : null);

            foreach (var item in ordered)
                action(item);
        }

        /// <summary>
        /// Records the pre-suffix display name a row must sort on - see <see cref="sortNames"/>'s
        /// own doc comment. Internal rather than private so ACE.Server.Tests can drive forEachItem's
        /// Name rung through the exact same code path production uses (InternalsVisibleTo,
        /// ACE.Server.csproj:15), since neither of the two production call sites is reachable from a
        /// unit test without a live account vault or client dats.
        /// </summary>
        internal void RegisterSortName(ObjectGuid guid, string baseName) => sortNames[guid] = baseName;

        public override bool TryGetItemForSale(ObjectGuid itemGuid, out WorldObject itemForSale)
        {
            return storeItems.TryGetValue(itemGuid, out itemForSale) || DefaultItemsForSale.TryGetValue(itemGuid, out itemForSale);
        }

        /// <summary>
        /// Fix round 1, I1: GameEventApproachVendor.cs computed numItems from the two base dictionaries
        /// directly and never saw storeItems, so an account holding only stored (non-ledger) items
        /// wrote numItems = 0 while forEachItem still serialized every one of them - the panel rendered
        /// empty for the single most common vault state. Routing the event through this virtual count
        /// instead makes the two structurally unable to disagree.
        /// </summary>
        public override int ItemsForSaleCount => storeItems.Count + DefaultItemsForSale.Count;

        /// <summary>
        /// Redundant guard (see class remarks): refuses and logs loudly if <paramref name="item"/> is
        /// one of this store's own biotas. The dictionary split is the real mitigation; this is what
        /// makes a future mistake NOISY instead of silent.
        ///
        /// BOTH collections of real biotas are checked, not just <see cref="storeItems"/>. Code review
        /// caught this: a stored item of a hidden ItemType lives only as a VALUE in
        /// <see cref="proxiedItems"/> (keyed by its proxy's guid, never its own), so a guard that
        /// consulted storeItems alone stopped covering exactly the items this change introduced -
        /// `storeItems.ContainsKey(item.Guid)` is false for every one of them. No production caller
        /// passes a vault biota here today (`GeneratorProfile.Spawn_Shop` is the only one), so this was
        /// a hole in the defence rather than a live bug; a guard that silently stops guarding half its
        /// subject matter is worth closing anyway, since its whole job is to be loud.
        ///
        /// Matched on GUID rather than by reference, so a re-materialized instance of the same stored
        /// object is caught too.
        /// </summary>
        public override void AddDefaultItem(WorldObject item)
        {
            // Every MEMBER of every proxied row is checked, not just each row's representative: a group
            // row's non-representative members are stored biotas too, and they are reachable from
            // nowhere else in this class.
            if (item != null && (storeItems.ContainsKey(item.Guid) || proxiedItems.Values.Any(members => members.Any(stored => stored.Guid == item.Guid))))
            {
                log.Error($"[MULE VENDOR] {Name}: refused to add 0x{item.Guid.Full:X8} ({item.Name}) to DefaultItemsForSale - it is one of this store's own biotas. WorldObject.Destroy only walks DefaultItemsForSale/UniqueItemsForSale (WorldObject.cs:921-927), so this would have made a stored item destroyable by a routine vendor Destroy.");
                return;
            }

            base.AddDefaultItem(item);
        }

        /// <summary>
        /// UniqueItemsForSale is always empty on a PersonalVendor (ProcessItemsForPurchase never
        /// populates it), so this has nothing to do. An inert override is the guarantee, not a
        /// comment: a base-class change to RotUniques can never resurrect the retail
        /// rot-and-destroy-after-300s path here.
        /// </summary>
        protected override void RotUniques()
        {
        }

        public override uint GetSellCost(WorldObject item) => 0;

        public override uint GetSellCost(Weenie item) => 0;

        public override int GetBuyCost(WorldObject item) => 0;

        public override int GetBuyCost(Weenie item) => 0;

        public override int CalculatePayoutCoinAmount(Dictionary<uint, WorldObject> items) => 0;

        /// <summary>
        /// The SELL direction (player -> vendor) is a deposit here. Retail destroys anything
        /// stackable and RemoveBiotaFromDatabase()'s what it keeps (Vendor.cs:717,739); this instead
        /// hands every STORABLE item to the store, destroying nothing and deleting no biota itself -
        /// whatever AccountVaultStore.TryDeposit does internally (ledger-collapse for a pristine item,
        /// or a vault add for anything else) is Task 4's contract, not a second implementation here.
        /// UniqueItemsForSale is never touched.
        ///
        /// THE ONE EXCEPTION is a trade note (ItemType.PromissoryNote), which is never storable and so
        /// is never offered to the store at all: <see cref="DepositItems"/> diverts it to the seller's
        /// banked pyreals at face value and Player.BankTradeNote destroys it directly, before the store
        /// is consulted. So this method does destroy an item on exactly that one path - see the branch
        /// at the top of DepositItems' loop for why the client's own price gate on withdrawal makes
        /// storing a note equivalent to confiscating it.
        /// </summary>
        public override void ProcessItemsForPurchase(Player player, Dictionary<uint, WorldObject> items)
        {
            DepositItems(VaultActor.From(player), items, player);

            // WaffleACE: SellPhase.Panel, the same charge base Vendor.ProcessItemsForPurchase makes at the
            // same point. Inert outside a sell handler scope - see SellPhaseProfile.
            var panelScope = SellPhaseProfile.Begin();

            ApproachVendor(player, VendorType.Sell);

            SellPhaseProfile.End(SellPhase.Panel, panelScope);
        }

        /// <summary>
        /// The testable core of <see cref="ProcessItemsForPurchase"/>: takes a <see cref="VaultActor"/>
        /// directly rather than requiring a live Player (constructing one needs
        /// DatabaseManager.Authentication, which is unavailable in a unit test - see
        /// PersonalVendorTests.cs). <paramref name="player"/> is used only for a player-facing error
        /// message and, on a refused deposit, to receive the item back - see
        /// <see cref="HandBackOrphan"/> - and may be null.
        ///
        /// Fix round 1, I2: by the time this runs, Player_Commerce.HandleActionSellItem has already
        /// detached the item from the player (cleared ContainerId) AND flushed that state to the shard.
        /// Retail's contract from this point on is "the vendor now owns it" - which is why base
        /// ProcessItemsForPurchase has no refusal branch at all. TryDeposit has five refusal paths
        /// reachable in normal play (store not ready, VaultAccess.None, UseBackpackSlot, still-parented,
        /// and vault full - the last one is reachable by any player at the entry cap), so a refusal here
        /// is not a rare edge case; every one of them now hands the item back via HandBackOrphan instead
        /// of leaving a detached, already-persisted orphan row.
        /// </summary>
        internal void DepositItems(VaultActor actor, Dictionary<uint, WorldObject> items, Player player)
        {
            // F7: named distinctly from the private `store` field. This local is deliberately
            // REASSIGNED by the I8 retry below and is not always the same value as the field at every
            // point in this method - a name identical to the field made that distinction live only in
            // a comment.
            var activeStore = Store;

            // WAVE 3 (SPEC-vault-batch-deposit.md section 5 step 6): every item that is NOT a trade note
            // and NOT refused for having no store is collected here and handed to the store in ONE
            // TryDepositBatch call inside ONE Enqueue, rather than one Enqueue/TryDeposit pair per item.
            // The trade-note divert and the no-store refusal both stay exactly where they were - AHEAD of
            // the store and unbatched - because neither one is a vault deposit at all.
            var depositCandidates = new List<WorldObject>(items.Count);

            foreach (var item in items.Values)
            {
                // TRADE NOTES ARE BANKED, NOT STORED, and the diversion happens here - before the
                // store is even consulted - because a note never becomes vault content at all.
                //
                // The reason is a CLIENT gate the server cannot move (and must not try to: the client
                // is never patched). Withdrawal is free on the server - GetSellCost/GetBuyCost are all
                // 0 and TryWithdrawTransaction charges nothing - but the vendor panel refuses to let a
                // player take a row whose Value they cannot afford in coin. A stored Trade Note
                // (250,000) therefore needs 250,000 pyreals in the pack to get back out, so a note
                // deposited into a vault is effectively confiscated from any player who is not already
                // carrying its face value.
                //
                // Crediting it to the bank is the same disposition /bank deposit notes already makes,
                // and it is exact rather than approximate: Player.BankTradeNote credits item.Value,
                // which is the whole stack's value, and the player can withdraw pyreals or notes back
                // out of the bank at will.
                if (item.ItemType == ItemType.PromissoryNote)
                {
                    // Never destroy a note without crediting it. BankTradeNote returns 0 for a null or
                    // non-positive Value and for a note that is somehow still parented, and destroys
                    // nothing in those cases, so a 0 here always means the note still exists and is
                    // still ours to return.
                    var credited = player?.BankTradeNote(item) ?? 0;

                    if (credited <= 0)
                    {
                        HandBackOrphan(item, player, "That trade note could not be banked.");
                        continue;
                    }

                    // Deliberately NOT an account_vault_log row: nothing entered the vault, and a
                    // vault audit row for an object with no vault entry is a reconciliation trap. The
                    // sale itself is already audited - Player_Commerce.HandleActionSellItem calls
                    // AnalyticsManager.RecordVendorSell per item before ProcessItemsForPurchase - so
                    // this line only records the disposition that audit row cannot show.
                    log.Info($"[VAULT] account {StoreAccountId}: banked trade note {item.WeenieClassId} x{item.StackSize ?? 1} value {credited} for {(player?.Name ?? "<no player>")}");

                    NumItemsBought++;
                    continue;
                }

                if (activeStore == null)
                {
                    HandBackOrphan(item, player, AccountVaultStore.UnavailableMessage);
                    continue;
                }

                depositCandidates.Add(item);
            }

            if (depositCandidates.Count == 0)
                return;

            var ok = false;
            IReadOnlyList<VaultDepositOutcome> outcomes = null;

            // TryDepositBatch's VaultActor overload asserts it is running on the store's own mutation
            // queue (AccountVaultStore.AssertOnMutationQueue) - Enqueue is what satisfies that,
            // and it blocks until the queued work has actually run (R3).
            //
            // WaffleACE: SellPhase.Deposit is charged INSIDE the work item rather than around Enqueue, so
            // it measures TryDepositBatch alone and is directly comparable with the out-of-process probe
            // that measured TryDeposit alone; the Enqueue/Drain overhead around it stays in
            // SellPhase.Purchase.
            //
            // THIS WORK ITEM DOES NOT ALWAYS RUN ON THE THREAD THAT ENQUEUES IT, and the profile is
            // world-thread-only, so the charge can simply not happen: Drain runs every queued item on
            // whichever thread won the store's drainLock, and AccountVaultBarrelReaper (and
            // AccountVaultFoldMigration) enqueue against this same per-account store from their own
            // background threads. Begin returns 0 there, so the time lands in the enclosing
            // SellPhase.Purchase instead - correct for the invariant, wrong for the attribution. That
            // undercount would be invisible, so it is counted: DepositCalls is read here, on the world
            // thread, and NoteDepositExecuted checks after Enqueue returns (it has run the work by then)
            // whether the expected charge landed. Same mechanism covers a throw out of TryDepositBatch,
            // which also skips the End; the `thrown` handling below logs that case separately, so the two
            // are distinguishable.
            var depositCallsBefore = SellPhaseProfile.DepositCalls;

            Action work = () =>
            {
                var depositScope = SellPhaseProfile.Begin();

                // WaffleACE wave 3: VaultDepositPhaseProfile's deposit scope is opened here rather than
                // inside AccountVaultStore.TryDeposit, because this closure calls TryDepositBatch
                // DIRECTLY (TryDeposit's own BeginDeposit/EndDeposit pair is never reached on this path).
                // EndBatchDeposit - not EndDeposit - is what keeps vd_n an ITEM count rather than
                // collapsing to 1 per sale; see its own remarks. Paired in a try/finally for the same
                // reason AccountVaultStore.TryDeposit pairs BeginDeposit/EndDeposit that way: a throw out
                // of TryDepositBatch (a real path - the CLASS/LEDGER STATE UNKNOWN arms) must not leave
                // the instrument armed for whatever the world thread runs next this tick.
                var vaultDepositScope = VaultDepositPhaseProfile.BeginDeposit();

                try
                {
                    ok = activeStore.TryDepositBatch(depositCandidates, actor, out outcomes);
                }
                finally
                {
                    VaultDepositPhaseProfile.EndBatchDeposit(vaultDepositScope, depositCandidates.Count);
                }

                SellPhaseProfile.End(SellPhase.Deposit, depositScope);
            };

            if (!activeStore.Enqueue(work, out var thrown))
            {
                // I8: Enqueue returns false, having run nothing, only when this store has already
                // been retired by the idle sweep - Enqueue's own doc comment names the recovery.
                // Re-fetch and retry once before treating this as a real failure. `activeStore` and
                // `work` are the same captured local the retry closure reads, so reassigning it
                // here is what the retry actually observes.
                //
                // WAVE 3: this is now a retry of the WHOLE BATCH rather than of one item, and that is
                // still safe by the identical argument the per-item retry relied on - Enqueue returning
                // false means the work item did NOT run at all, so nothing in this batch has touched the
                // store yet and every candidate is provably still the caller's to hand back.
                activeStore = AccountVaultManager.GetStore(StoreAccountId);
                Store = activeStore;

                if (activeStore == null || !activeStore.Enqueue(work, out thrown))
                {
                    foreach (var item in depositCandidates)
                        HandBackOrphan(item, player, AccountVaultStore.UnavailableMessage);

                    return;
                }
            }

            // Reached only where the work item HAS run - both refused-Enqueue arms above `return` - so
            // exactly one deposit call is accounted for here, either as a SellPhase.Deposit charge or as an
            // unmeasured one. Inert outside a sell handler scope.
            SellPhaseProfile.NoteDepositExecuted(depositCallsBefore);

            // Fix round 2, F4, generalized to a batch in wave 3: a THROW is not a refusal, and must not be
            // handed back.
            //
            // Drain catches a work item's exception without rethrowing, so `ok` is still at its
            // initialized false. TryDepositBatch can throw AFTER it has already credited one or more
            // groups (phase C's world.DestroyItem tail, or a backend batch call that commits before a
            // LATER group's call throws) or after an item is physically in a vault
            // container (DepositToVault's save tail). Handing ANY item back on top of that would risk the
            // dupe the deposit ordering exists to prevent.
            //
            // THE RULE THAT MATTERS MOST (AccountVaultStore.TryDepositBatch guarantee 1b): an item whose
            // group's credit committed must NEVER reach HandBackOrphan, including here. `outcomes` is
            // assigned by TryDepositBatch BEFORE any work runs - see VaultDepositOutcome's own remarks -
            // so it is safe and correct to read even though the call that filled it threw partway
            // through.
            //
            // THE DISPOSITION IS PER ITEM, NOT PER BATCH, and that is the correction to the first
            // generalization of the single-item rule. Withholding EVERY item unconditionally is only the
            // right answer when every item might have been credited. In a batch that is false: an item a
            // phase A guard or the cap REFUSED, or one whose group the database provably refused, is at
            // Commit == None, and None is written by nothing downstream of a credit - the in-flight
            // Unknown mark for a deferred group is written BEFORE the statement that credits it runs, and
            // an item routed to a vault container is Committed the moment it lands. So None after a throw
            // provably means no credit was even attempted for this item, and withholding it is not
            // caution, it is LOSS: the item is detached, undestroyed, in no container and never returned,
            // which is the one state outside all three guarantee 1 enumerates.
            //
            // Committed and Unknown are both still withheld, for the original reason. A committed item is
            // the vault's. An unknown one is not PROVABLY safe to hand back either - the throw could have
            // landed after its own group's credit and before that group's outcome record caught up - so
            // the same answer covers both without needing to prove which is which.
            //
            // A NULL OR SHORT `outcomes`, or a null record, is NOT the None case and stays conservative.
            // It means the batch never got far enough to say anything, which is an absence of information
            // rather than the positive statement None makes.
            if (thrown != null)
            {
                for (var i = 0; i < depositCandidates.Count; i++)
                {
                    var item = depositCandidates[i];
                    var outcome = outcomes != null && i < outcomes.Count ? outcomes[i] : null;

                    if (outcome != null && !outcome.Deposited && outcome.Commit == VaultDepositCommit.None)
                    {
                        // Provably untouched. HandBackOrphan still applies its own IsAlreadyParented
                        // guard, so even a record that is wrong about this cannot double-parent anything.
                        HandBackOrphan(item, player, outcome.FailReason);
                        continue;
                    }

                    var committed = outcome != null && (outcome.Deposited || outcome.Commit == VaultDepositCommit.Committed);

                    log.Error($"[MULE VENDOR] DEPOSIT STATE {(committed ? "COMMITTED" : "UNKNOWN")} for account {StoreAccountId}: queued vault work for 0x{item.Guid.Full:X8} ({item.Name}), sold by {(player?.Name ?? "<no player>")} (0x{player?.Guid.Full ?? 0:X8}), threw. It may have been credited to the ledger, a class row or a vault container before it stopped, so it is NOT being handed back - doing that on top of a credit would duplicate it. Reconcile against account_vault_log.");
                }

                player?.SendTransientError(AccountVaultStore.UnavailableMessage);
                return;
            }

            // The batch call returned normally - every outcome is therefore finalized (Refused, Unknown-
            // without-a-throw, or Committed/Stored; see AccountVaultStore.TryDepositBatch's own remarks on
            // why a DB-level "Failed" result still resolves without throwing). Deposited is the single
            // authoritative per-item answer here, exactly as `ok` was for the single-item path: true only
            // for Committed/Stored, false for both Refused and Unknown, both of which leave the item
            // undestroyed and needing a hand-back - the store never returns an undetached item to the
            // player's pack itself.
            for (var i = 0; i < depositCandidates.Count; i++)
            {
                var item = depositCandidates[i];
                var outcome = outcomes[i];

                if (!outcome.Deposited)
                {
                    HandBackOrphan(item, player, outcome.FailReason);
                    continue;
                }

                // Only after a genuinely successful disposition - base Vendor.ProcessItemsForPurchase
                // increments NumItemsBought after a successful resell-or-destroy decision, never for a
                // rejected sale (M4).
                NumItemsBought++;
            }
        }

        /// <summary>
        /// Hands an undepositable item back to the player who tried to sell it, rather than leaving it
        /// an orphan row (fix round 1, I2). Never destroys it - the sell path already detached and
        /// flushed it, so this is the item's only way home. Logs an ORPHAN line if even the hand-back
        /// fails, which needs manual recovery.
        /// </summary>
        private void HandBackOrphan(WorldObject item, Player player, string reason)
        {
            // Fix round 2, F8: StoreAccountId is 0 ONLY when no store is bound (see its own remarks) -
            // exactly the case where "account 0's vault" would otherwise be logged, which is not
            // actionable. Every other call site has a real, bound store and a real account id.
            var accountDescription = StoreAccountId == 0 ? "no store bound" : $"account {StoreAccountId}'s vault";

            // Fix round 2, F4: an item that is ALREADY somewhere is not an orphan and must not be
            // handed back. This is the same "already home" reading ReturnAll applies on the withdraw
            // side, arrived at the same way: a refusal reported for an item that is nevertheless
            // parented means the mutation got further than its return value admits, and handing it back
            // on top of that puts one object in two containers at once. TryReturnWithdrawn,
            // TryReturnWithdrawnToLedger and ReturnAll have all carried this guard from the start; the
            // deposit-side hand-back was the one place that called
            // TryCreateInInventoryWithNetworking with no check at all.
            if (IsAlreadyParented(item))
            {
                log.Warn($"[MULE VENDOR] {Name}: 0x{item.Guid.Full:X8} ({item.Name}) for {accountDescription} is still parented (container 0x{item.ContainerId:X8}, wielder 0x{item.WielderId:X8}) after a deposit that reported failure: {reason}. It is already somewhere, so it is NOT being handed back - doing so would put one object in two containers. Not treating it as an orphan.");
                player?.SendTransientError(reason ?? AccountVaultStore.UnavailableMessage);
                return;
            }

            log.Error($"[MULE VENDOR] {Name}: deposit of 0x{item.Guid.Full:X8} ({item.Name}) into {accountDescription} failed: {reason}. Returning it to {(player?.Name ?? "<no player>")} rather than leaving a detached, already-persisted orphan row.");
            player?.SendTransientError(reason ?? AccountVaultStore.UnavailableMessage);

            if (!TryHandBack(item, player))
                log.Error($"[MULE VENDOR] ORPHAN: 0x{item.Guid.Full:X8} ({item.Name}) could not be returned to {(player?.Name ?? "<no player>")} after a refused deposit. It is detached and needs manual recovery.");
            else
                item.SaveBiotaToDatabase();
        }

        /// <summary>
        /// Fix round 2, F4: whether this object is already in a container or on a body, and therefore
        /// is not anybody's to place. The one rule, in one place, so the seam below and its caller
        /// cannot drift - the withdraw side reached the same test independently at three separate
        /// sites, which is how the deposit side's missing copy stayed invisible.
        /// </summary>
        private static bool IsAlreadyParented(WorldObject item)
        {
            return item != null && (item.ContainerId != null || item.WielderId != null);
        }

        /// <summary>
        /// Fix round 2, F3: the actual placement attempt, extracted to its own seam so a test can
        /// observe that the hand-back genuinely RAN rather than merely inferring it from side effects
        /// that look identical whether it ran or not. With a null player, the previous inline form's
        /// only observable effect was a log line - which the pre-fix code (with no hand-back at all)
        /// also produced by a different path, so a test asserting only "not destroyed" and "refused"
        /// passed with this whole mechanism deleted. `private protected virtual` so the test project's
        /// subclass (via InternalsVisibleTo) can override and record the call without exposing this as
        /// public API. `protected` rather than `private protected`: a test subclass in a different
        /// assembly (ACE.Server.Tests) must be able to override it, and `private protected` restricts
        /// overriding to a derived type in the SAME assembly regardless of InternalsVisibleTo (that
        /// attribute only lifts `internal` access, not `private protected`).
        /// </summary>
        protected virtual bool TryHandBack(WorldObject item, Player player)
        {
            // Fix round 2, F4: the enforcing copy of HandBackOrphan's guard. Kept here as well as
            // there because this is the seam - a future caller reaching it directly would otherwise
            // place an object that is already in a container, and TryCreateInInventoryWithNetworking
            // does not check.
            if (IsAlreadyParented(item))
            {
                log.Warn($"[MULE VENDOR] {Name}: refusing to hand back 0x{item.Guid.Full:X8} ({item.Name}) because it is still parented (container 0x{item.ContainerId:X8}, wielder 0x{item.WielderId:X8}).");
                return false;
            }

            return player != null && player.TryCreateInInventoryWithNetworking(item);
        }

        /// <summary>
        /// GATE 2's authorization check (DESIGN section 10), extracted from
        /// <see cref="TryWithdrawTransaction"/> as its own testable core - split out in the
        /// reconciliation fix (Task 12) specifically so a test can observe the exact failReason a
        /// grant-read failure produces without needing a live Player (TryWithdrawTransaction's own
        /// failure paths are only observable through `player?.SendTransientError`, which needs a real
        /// Session and NREs against a reflection-built one - see PersonalVendorTests.cs class remarks
        /// and MuleSummonTests.cs's identical constraint). Requires DepositWithdraw specifically, unlike
        /// <see cref="TryAuthorize"/> (gate 1), which only requires SOME access - a deposit-only
        /// grantee may open the panel but may not withdraw.
        /// </summary>
        internal bool TryAuthorizeWithdraw(VaultActor actor, out VaultAccess access, out string failReason)
        {
            var activeStore = Store;

            if (activeStore == null)
            {
                access = VaultAccess.None;
                failReason = AccountVaultStore.UnavailableMessage;
                return false;
            }

            if (!activeStore.IsLoaded)
            {
                access = VaultAccess.None;
                failReason = AccountVaultStore.StillLoadingMessage;
                return false;
            }

            // Reconciliation fix (Task 12): was AccountVaultStore.GetAccess, which collapses a
            // grant-read FAILURE into VaultAccess.None - a database outage was reported to the player
            // as "you do not have permission", the most alarming thing this feature can say to someone
            // with items stored. TryGetAccess distinguishes the two, matching CanAcceptCore's own
            // pattern (F4). Security is unaffected either way: AccountVaultStore.TryWithdraw's own
            // unconditional check (around :1519) still refuses the transaction regardless of what this
            // gate reports.
            if (!activeStore.TryGetAccess(actor, out access, out failReason))
                return false;

            if (access != VaultAccess.DepositWithdraw)
            {
                failReason = access == VaultAccess.None
                    ? "You do not have permission to use this vault."
                    : "You do not have permission to withdraw from this vault.";
                return false;
            }

            failReason = null;
            return true;
        }

        /// <summary>
        /// The BUY direction (vendor -> player) is a withdrawal here. See
        /// <see cref="TryWithdrawTransaction"/> for the real logic and for GATE 2 (DESIGN section 10),
        /// one layer of the withdrawal authorization - see that method's remarks for why it is defence
        /// in depth rather than the sole boundary.
        /// </summary>
        public override bool BuyItems_ValidateTransaction(List<ItemProfile> itemProfiles, Player player)
        {
            return TryWithdrawTransaction(itemProfiles, VaultActor.From(player), player);
        }

        /// <summary>
        /// The testable core of <see cref="BuyItems_ValidateTransaction"/>: takes a
        /// <see cref="VaultActor"/> directly so authorization refusal can be exercised without a live
        /// Player (see PersonalVendorTests.cs). <paramref name="player"/> drives delivery and
        /// player-facing messages and may be null - the capacity/burden check below is SKIPPED, not
        /// failed, for a null player (there is nothing to check against), so a null player with an
        /// otherwise-valid, authorized basket DOES reach the withdraw loop and delivery. Delivery itself
        /// treats "no player" the same as "could not place it" and returns everything to the vault
        /// instead (see the finally block below) - it is never left withdrawn-but-undelivered, and the
        /// trailing <see cref="ApproachVendor"/> call is itself null-guarded (fix round 1, M3 and I5).
        ///
        /// GATE 2 (DESIGN section 10): re-resolved HERE, on every call, independent of whether
        /// CheckUseRequirements (gate 1) ever ran for this actor - both HandleActionBuyItem and
        /// HandleActionSellItem resolve the vendor purely by guid off the landblock
        /// (Player_Commerce.cs:39, :216) and never read back player.LastOpenedContainerId, so a crafted
        /// GameActionBuyItems carrying this vendor's guid - trivially obtained, since the vendor is
        /// broadcast to everyone nearby - would otherwise reach this method for a store the sender was
        /// never authorized to see.
        ///
        /// Fix round 2, corrected doc claim: this gate is defence in depth, NOT the sole security
        /// boundary it was previously described as. AccountVaultStore.TryWithdraw performs its own,
        /// unconditional access check (AccountVaultStore.cs, around TryGetAccess/access !=
        /// VaultAccess.DepositWithdraw, immediately before dispatching to either private withdraw
        /// method) on every call regardless of whether this gate ran - confirmed by disabling this
        /// gate's block and observing the refusal tests still pass, driven entirely by the store's own
        /// check. This gate stays: it produces the vendor's own player-facing message before ever
        /// reaching the store, and layering an independent check here is the same reasoning CanAccept
        /// applies for deposits. It is a second lock on the same door, not the only one.
        /// </summary>
        internal bool TryWithdrawTransaction(List<ItemProfile> itemProfiles, VaultActor actor, Player player)
        {
            if (!TryAuthorizeWithdraw(actor, out _, out var authFailReason))
            {
                player?.SendTransientError(authFailReason);
                return false;
            }

            // F7: named distinctly from the private `store` field, matching DepositItems - this local
            // is reassigned by the I8 retry below. Re-read after TryAuthorizeWithdraw rather than
            // threaded through it, since Store is a property and TryAuthorizeWithdraw's own readiness
            // check already proved it non-null and loaded by the time control reaches here.
            var activeStore = Store;

            if (itemProfiles == null || itemProfiles.Count == 0)
                return false;

            var requests = new List<(VaultEntry entry, int amount)>();

            // Parallel to `requests`, index for index: which panel row each request named and what kind
            // of row it is. Read only after a successful withdrawal, to decide whether the panel refresh
            // can be deferred and to keep this window's lookups live if it is - see
            // PrepareWithdrawRefresh.
            var requestRows = new List<(ObjectGuid displayGuid, MuleWithdrawRowKind kind)>();

            // Mirrors VerifySellItems's own duplicate guard. Without it a basket naming one ledger row
            // twice withdraws the first amount, has the second refused by the ledger's non-negative SQL
            // guard, and then unwinds the first - which is the path that converts a single ledger entry
            // into an uncapped stored-biota entry.
            var seenProfiles = new HashSet<uint>();

            foreach (var itemProfile in itemProfiles)
            {
                if (!itemProfile.IsValidAmount)
                {
                    player?.SendTransientError("Invalid amount");
                    return false;
                }

                if (!seenProfiles.Add(itemProfile.ObjectGuid))
                {
                    player?.SendTransientError("You cannot withdraw the same row twice in one transaction.");
                    return false;
                }

                var itemGuid = new ObjectGuid(itemProfile.ObjectGuid);

                // A proxied item's row carries the PROXY's guid, so that is what the client sends back
                // rather than the stored biota's. It resolves here, identically: a proxy is a
                // presentation detail and the thing being withdrawn is the same indivisible stored
                // biota either way. This MUST be tried before the ledger branch below, because a proxy
                // also lives in DefaultItemsForSale - falling through would build a ForLedger request
                // and withdraw from a collapsed stack instead of handing over this item.
                WorldObject stored = null;
                IReadOnlyList<WorldObject> members = null;

                if (storeItems.TryGetValue(itemGuid, out var storedDirect))
                    stored = storedDirect;
                else if (proxiedItems.TryGetValue(itemGuid, out var proxied))
                {
                    members = proxied;

                    if (proxied.Count == 1)
                        stored = proxied[0];
                }

                // A GROUP row is several equivalent stored biotas on one line, and its amount means
                // "how many MEMBERS", so it is the one stored-side row that accepts a partial request.
                // Resolved before the singleton branch below because both live in proxiedItems.
                if (members != null && members.Count > 1)
                {
                    // Refused rather than clamped, so a player asking for more than the row holds is
                    // told so. The store re-checks this against the live vaults; this is the vendor's
                    // own player-facing half, the same layering the authorization gates use.
                    if (itemProfile.Amount > members.Count)
                    {
                        player?.SendTransientError($"Your vault holds only {members.Count} of those.");
                        return false;
                    }

                    requests.Add((VaultEntry.ForGroup(members), itemProfile.Amount));
                    requestRows.Add((itemGuid, MuleWithdrawRowKind.Group));
                }
                else if (stored != null)
                {
                    // Fix round 1, I7: VaultEntry.ForItem sets Count to the item's own StackSize, so
                    // passing that same value back as "amount" made the store's amount != entry.Count
                    // guard equal by construction - it could never fire, and a player's requested
                    // partial amount was silently discarded in favor of the whole stack. A stored biota
                    // is not divisible (it did not collapse into the ledger precisely because it is not
                    // a plain, identical-to-fresh stack), so the only valid request is the whole thing;
                    // anything else is refused rather than silently rounded up.
                    var whole = stored.StackSize ?? 1;

                    if (itemProfile.Amount != whole)
                    {
                        player?.SendTransientError("A stored item can only be withdrawn whole.");
                        return false;
                    }

                    requests.Add((VaultEntry.ForItem(stored), whole));
                    requestRows.Add((itemGuid, MuleWithdrawRowKind.Individual));
                }
                else if (classDisplays.TryGetValue(itemGuid, out var classRow))
                {
                    // BEFORE the ledger arm below, and it is the same hazard the proxy arm above
                    // describes: a class display also lives in DefaultItemsForSale, so falling through
                    // would build a ForLedger request and debit the collapsed STACK ledger for this
                    // wcid - a different holding entirely.
                    if (itemProfile.Amount > classRow.Count)
                    {
                        player?.SendTransientError($"Your vault holds only {classRow.Count} of those.");
                        return false;
                    }

                    requests.Add((classRow, itemProfile.Amount));
                    requestRows.Add((itemGuid, MuleWithdrawRowKind.Class));
                }
                else if (DefaultItemsForSale.TryGetValue(itemGuid, out var ledgerDisplay))
                {
                    requests.Add((VaultEntry.ForLedger(ledgerDisplay.WeenieClassId, itemProfile.Amount), itemProfile.Amount));
                    requestRows.Add((itemGuid, MuleWithdrawRowKind.Ledger));
                }
                else
                {
                    player?.SendTransientError("That is no longer in your vault.");
                    return false;
                }
            }

            // Capacity/burden precheck, mirroring Vendor.BuyItems_ValidateTransaction
            // (Vendor.cs:479-511) - reject atomically BEFORE anything is withdrawn, rather than
            // withdrawing some entries and failing partway through. SKIPPED (not failed) for a null
            // player - there is nothing to check a capacity limit against.
            if (player != null)
            {
                var itemsToReceive = new ItemsToReceive(player);

                foreach (var (entry, amount) in requests)
                {
                    itemsToReceive.Add(entry.Wcid, amount);

                    if (itemsToReceive.PlayerExceedsLimits)
                        break;
                }

                if (itemsToReceive.PlayerExceedsAvailableBurden)
                {
                    player.SendTransientError("You are too encumbered to withdraw that!");
                    return false;
                }

                if (itemsToReceive.PlayerOutOfInventorySlots || itemsToReceive.PlayerOutOfContainerSlots)
                {
                    player.SendTransientError("You do not have enough pack space to withdraw that!");
                    return false;
                }
            }

            // Fix round 1, I3 (and the coordinator's exception-safety amendment to it): everything
            // withdrawn from here on is tracked in `delivered`, and the finally block below returns
            // whatever is still undischarged when this method exits, THROUGH ANY EXIT - a mid-basket
            // `return false`, or an exception thrown by TryCreateInInventoryWithNetworking or
            // SaveBiotaToDatabase. AccountVaultStore.TryWithdraw has already removed a stored item from
            // its vault container and saved the container by the time it returns; it deliberately does
            // NOT save the item itself, so an item left undischarged here is not yet lost - but without
            // this unwind it IS leaked (gone from the in-memory vault, gone from the panel, in nobody's
            // inventory), and a later store eviction/rehydration resurrects the same guid from the row
            // that still points at the vault - a live duplicate of an object already handed out. That is
            // exactly the dupe TryWithdraw's own contract exists to prevent.
            // The provenance flag is load-bearing, not bookkeeping: the unwind below has to send a
            // ledger-derived object back to the LEDGER and a stored biota back to a vault container,
            // and nothing about the object itself distinguishes them once it has been built.
            var delivered = new List<(WorldObject item, bool fromLedger, uint wcid)>();
            var discharged = new HashSet<WorldObject>();

            // Per request, what the store handed back - read after delivery to tell a partial row
            // decrement from an emptied row (see PrepareWithdrawRefresh).
            var withdrawnPerRequest = new List<IReadOnlyList<WorldObject>>(requests.Count);

            // Set when anything withdrawn could not be placed and went back into the vault instead. The
            // vault's shape then moved in a way the deferred-refresh lookup patch does not model, so the
            // panel is refreshed immediately rather than deferred.
            var anyReturnedToVault = false;

            try
            {
                foreach (var (entry, amount) in requests)
                {
                    var ok = false;
                    List<WorldObject> withdrawn = null;
                    string failReason = null;

                    Action work = () => ok = activeStore.TryWithdraw(entry, amount, actor, out withdrawn, out failReason);

                    if (!activeStore.Enqueue(work, out var thrown))
                    {
                        // I8: re-fetch and retry once, same recovery as DepositItems - Enqueue's own
                        // doc comment names it. `activeStore` is the same captured local `work` reads,
                        // so reassigning it here is what the retry actually observes.
                        activeStore = AccountVaultManager.GetStore(StoreAccountId);
                        Store = activeStore;

                        if (activeStore == null || !activeStore.Enqueue(work, out thrown))
                        {
                            player?.SendTransientError("Withdrawal failed: your vault is temporarily unavailable.");
                            return false;
                        }
                    }

                    // Fix round 2, F4, the withdraw mirror. A throw here is NOT "the withdraw was
                    // refused, nothing left the vault": TryWithdraw removes a stored item from its
                    // container, and debits the ledger, well before it returns, so by the time
                    // something in its tail throws the objects may already exist and the vault may
                    // already be short.
                    //
                    // `withdrawn` is an OUT parameter, so anything TryWithdraw put in it before
                    // throwing is visible here even though the call did not complete - the reference is
                    // to this method's own local. Those objects are live, unparented and in nobody's
                    // inventory, so they are enrolled in `delivered` and the finally block below
                    // returns them to the vault exactly as it would for any other mid-basket failure.
                    // Treating them as undelivered and simply returning would leak them: gone from the
                    // in-memory vault, gone from the panel, held by no one, and resurrected as a live
                    // duplicate the next time the store rehydrates from the row that still points at
                    // the vault.
                    if (thrown != null)
                    {
                        log.Error($"[MULE VENDOR] WITHDRAW STATE UNKNOWN for account {StoreAccountId}: queued vault work for wcid {entry.Wcid} (amount {amount}, {entry.Kind}) threw after removing or debiting. {(withdrawn?.Count ?? 0)} object(s) came back through the out parameter and are being returned to the vault; anything it built and did not hand back is unreachable and needs reconciling against account_vault_log.");

                        if (withdrawn != null)
                        {
                            foreach (var item in withdrawn)
                                delivered.Add((item, entry.Kind == VaultEntryKind.Ledger, entry.Wcid));
                        }

                        player?.SendTransientError("Withdrawal failed: your vault is temporarily unavailable.");
                        return false;
                    }

                    if (!ok)
                    {
                        player?.SendTransientError(failReason ?? "Withdrawal failed.");
                        return false;
                    }

                    // `fromLedger` is EXACTLY "this came out of the collapsed stack ledger", never
                    // "this has no biota behind it". An item withdrawn from a counted CLASS row also
                    // has no shard row, but it must NOT be credited back to the stack ledger, which is
                    // a different holding of the same wcid. It is returned as an ordinary stored biota
                    // instead: the object is real, ReturnWithdrawn puts it in a vault container and
                    // saves it, and the lazy fold collapses it back into its class later. That costs
                    // one entry until the fold runs and loses nothing.
                    foreach (var item in withdrawn)
                        delivered.Add((item, entry.Kind == VaultEntryKind.Ledger, entry.Wcid));

                    withdrawnPerRequest.Add(withdrawn);
                }

                // Deliver PROMPTLY and SAVE what is delivered. AccountVaultStore.TryWithdraw hands back
                // live objects that are in nobody's inventory and are NOT saved - the row still points
                // at the vault until this places the item somewhere else, which is deliberate:
                //
                // a crash in this window returns a STORED item to the vault on restart, because its row
                // still points at the vault container. That is NOT true of a ledger-derived stack: it
                // has no row at all until the save below lands, so a crash destroys those units. The
                // audit row written before the debit is what makes that case reconstructable.
                foreach (var (item, fromLedger, wcid) in delivered)
                {
                    if (player != null && player.TryCreateInInventoryWithNetworking(item))
                    {
                        // Discharged the MOMENT it is physically in the player's pack, before the save
                        // below - not after. TryReturnWithdrawn refuses anything still parented
                        // (item.ContainerId != null), so if SaveBiotaToDatabase threw and this item were
                        // left undischarged, the finally unwind would try to return an item that is
                        // sitting in the player's inventory, which TryReturnWithdrawn would (correctly)
                        // refuse - but only after logging a confusing ORPHAN for an item that was never
                        // actually lost. Marking it discharged here means a save failure is a save
                        // failure, not mistaken for an undelivered withdrawal.
                        discharged.Add(item);

                        // Threads cleanup layer (b): a gem bound to a run that is no longer live
                        // never actually reaches the player - it is destroyed the instant it lands in
                        // their pack. `continue` skips the SaveBiotaToDatabase call below on purpose: the
                        // item no longer exists, there is nothing to persist, and saving a destroyed
                        // object's biota would log a spurious WITHDRAW SAVE FAILED for an item that was
                        // never meant to survive delivery. It is already `discharged`, so the finally
                        // block's undelivered-item unwind leaves it alone.
                        if (ACE.Server.ThreadDungeons.ThreadDungeonSweeper.IsDeadBoundGem(item))
                        {
                            var deadGemName = item.Name;
                            player.TryConsumeFromInventoryWithNetworking(item);
                            player.Session?.Network.EnqueueSend(new GameMessageSystemChat($"Your {deadGemName} crumbles to dust; its dungeon is gone.", ChatMessageType.Broadcast));
                            continue;
                        }

                        // The callback arrives on the database worker thread, NOT this one. It may only
                        // log - touching store state from here would race every mutation on the queue.
                        // The account id is captured HERE for the same reason the guid and name are:
                        // StoreAccountId reads `store?.AccountId ?? 0`, an unsynchronized read of this
                        // vendor's own field, and WorldObject.Destroy's mule branch sets Store = null on
                        // logout - so a vendor destroyed between the save and its callback would have
                        // logged "account 0", losing the one identifier a manual reconciliation needs.
                        var savedGuid = item.Guid.Full;
                        var savedName = item.Name;
                        var savedAccountId = StoreAccountId;

                        activeStore.World.SaveBiota(item, ok =>
                        {
                            if (!ok)
                                log.Error($"[VAULT] WITHDRAW SAVE FAILED for account {savedAccountId}: 0x{savedGuid:X8} ({savedName}) was handed to the player but its biota did not persist. If the ledger row was already debited this is a permanent loss and needs manual reconciliation against account_vault_log.");
                        });
                    }
                    else
                    {
                        log.Error($"[MULE VENDOR] {Name}: could not place withdrawn item 0x{item.Guid.Full:X8} ({item.Name}) into {(player?.Name ?? "<no player>")}'s inventory; handing it back to the vault rather than losing or destroying it.");

                        anyReturnedToVault = true;

                        // Only marked discharged on a SUCCESSFUL return. A failed one is deliberately
                        // left in `delivered` so the finally block's unwind gets one more attempt at it
                        // rather than the failure being swallowed here.
                        if (ReturnOne(activeStore, (item, fromLedger, wcid), actor))
                            discharged.Add(item);
                    }
                }

                // Previously an unconditional ApproachVendor(player, VendorType.Buy) - a full re-send of
                // the vendor list after every withdrawal. Now deferred (a fixed window) when the basket
                // only partially decremented class/group/ledger rows; immediate otherwise. See
                // PersonalVendor_WithdrawRefresh.cs.
                RefreshAfterWithdraw(player, activeStore, requests, requestRows, withdrawnPerRequest, anyReturnedToVault);

                return true;
            }
            finally
            {
                // Covers a mid-basket `return false` above AND any exception from anywhere in the try
                // block - everything in `delivered` that was not itself placed in the player's pack or
                // already handed back above is returned to the vault here. Never Destroy() - see class
                // remarks.
                var undischarged = delivered.Where(entry => !discharged.Contains(entry.item)).ToList();

                if (undischarged.Count > 0)
                    ReturnAll(activeStore, undischarged, actor);
            }
        }

        /// <summary>
        /// Returns one withdrawn item to the vault. Returns whether the return succeeded; a caller
        /// that gets false back must NOT treat the item as discharged - it is still live, still
        /// undelivered, and still the caller's to account for. Never Destroy()s it either way.
        /// </summary>
        private bool ReturnOne(AccountVaultStore targetStore, (WorldObject item, bool fromLedger, uint wcid) entry, VaultActor actor)
        {
            var returnedOk = false;

            Action work = entry.fromLedger
                ? () => returnedOk = targetStore.TryReturnWithdrawnToLedger(entry.item, entry.wcid, entry.item.StackSize ?? 1, actor)
                : () => returnedOk = targetStore.TryReturnWithdrawn(entry.item, actor);

            if (targetStore == null || !targetStore.Enqueue(work) || !returnedOk)
            {
                log.Error($"[MULE VENDOR] {Name}: could not return 0x{entry.item.Guid.Full:X8} ({entry.item.Name}) to account {StoreAccountId}'s vault on the first attempt. Not yet an orphan - it is still live and will be retried.");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Returns every item in <paramref name="items"/> to the vault in one enqueue, logging an
        /// ORPHAN line naming each guid that could not be returned. Never Destroy()s any of them.
        /// </summary>
        private void ReturnAll(AccountVaultStore targetStore, List<(WorldObject item, bool fromLedger, uint wcid)> items, VaultActor actor)
        {
            if (items.Count == 0)
                return;

            if (targetStore == null)
            {
                foreach (var (item, _, _) in items)
                    log.Error($"[MULE VENDOR] ORPHAN: 0x{item.Guid.Full:X8} ({item.Name}) could not be returned to the vault for account {StoreAccountId} - no store is bound. It has NOT been destroyed and needs manual recovery.");

                return;
            }

            var results = new Dictionary<WorldObject, bool>();

            var enqueued = targetStore.Enqueue(() =>
            {
                // Same provenance branch as ReturnOne, and for the same reason: a ledger-derived object
                // filed back as a stored biota is an uncapped entry, not the entry it came from.
                foreach (var entry in items)
                    results[entry.item] = entry.fromLedger
                        ? targetStore.TryReturnWithdrawnToLedger(entry.item, entry.wcid, entry.item.StackSize ?? 1, actor)
                        : targetStore.TryReturnWithdrawn(entry.item, actor);
            });

            foreach (var (item, _, _) in items)
            {
                if (!enqueued || !results.TryGetValue(item, out var ok) || !ok)
                {
                    // F6: a still-parented item here means TryReturnWithdrawn's own TryAddToInventory
                    // already succeeded - the item IS physically back in the vault - and something
                    // AFTER that (SaveBiota, WriteLog) threw, which Drain catches and logs, leaving
                    // `results` never populated for this item even though nothing was lost. Logging
                    // ORPHAN at Error here would send an admin on a recovery hunt for an item that is
                    // not missing; TryReturnWithdrawn's own refusal for a still-parented item confirms a
                    // retry would find it already home, not repeat the return.
                    if (item.ContainerId != null)
                        log.Warn($"[MULE VENDOR] {Name}: 0x{item.Guid.Full:X8} ({item.Name}) for account {StoreAccountId} is still parented (container 0x{item.ContainerId:X8}) after a return that reported failure - the earlier attempt appears to have actually completed. Not treating it as an orphan.");
                    else
                        log.Error($"[MULE VENDOR] ORPHAN: 0x{item.Guid.Full:X8} ({item.Name}) could not be returned to the vault for account {StoreAccountId}{(enqueued ? "" : " - the store's mutation queue refused the work (store evicted?)")}. It has NOT been destroyed and needs manual recovery.");
                }
            }
        }

        /// <summary>
        /// Gate 1 (DESIGN section 10): UX only. Refuses an unauthorized player with a clear message
        /// rather than showing an empty panel - DESIGN rejected an empty panel as indistinguishable
        /// from a bug and as an invitation to a view-level filter that would leave gate 2 unguarded. A
        /// deposit-only grantee IS authorized here; only withdrawal (gate 2,
        /// <see cref="TryWithdrawTransaction"/>) requires DepositWithdraw specifically. Copies the
        /// shape of Storage.CheckUseRequirements (Storage.cs:48-75).
        /// </summary>
        public override ActivationResult CheckUseRequirements(WorldObject activator)
        {
            var baseRequirements = base.CheckUseRequirements(activator);
            if (!baseRequirements.Success)
                return baseRequirements;

            if (!(activator is Player player))
                return new ActivationResult(false);

            if (!TryAuthorize(VaultActor.From(player), out _, out var failReason))
            {
                player.SendTransientError(failReason);
                return new ActivationResult(false);
            }

            return new ActivationResult(true);
        }

        /// <summary>
        /// The testable core of gate 1: takes a <see cref="VaultActor"/> directly so refusal/allow can
        /// be exercised without a live Player. Re-resolved on every call, with no caching, via
        /// AccountVaultStore.TryGetAccess - DESIGN section 10 requires a revoke to take effect
        /// immediately with no session-invalidation logic.
        /// </summary>
        internal bool TryAuthorize(VaultActor actor, out VaultAccess access, out string failReason)
        {
            var store = Store;

            if (store == null)
            {
                access = VaultAccess.None;
                failReason = AccountVaultStore.UnavailableMessage;
                return false;
            }

            if (!store.IsLoaded)
            {
                access = VaultAccess.None;
                failReason = AccountVaultStore.StillLoadingMessage;
                return false;
            }

            // Reconciliation fix (Task 12): was AccountVaultStore.GetAccess, which collapses a
            // grant-read FAILURE into VaultAccess.None - see TryWithdrawTransaction's identical remark
            // above for the full reasoning. TryGetAccess's own failReason is used directly on a false
            // return, matching CanAcceptCore's pattern (F4).
            if (!store.TryGetAccess(actor, out access, out failReason))
                return false;

            if (access == VaultAccess.None)
            {
                failReason = "You do not have permission to use this vault.";
                return false;
            }

            failReason = null;
            return true;
        }

        /// <summary>
        /// Fix round 1 (post-I2 addendum): NON-MUTATING preflight so a refused deposit never detaches
        /// the item from the player's pack in the first place. I2's hand-back (HandBackOrphan) stays
        /// required regardless - this check is TOCTOU by nature, since another window on the same store
        /// can fill it between this call and the real deposit (R3 is exactly that scenario), so a later
        /// refusal from TryDeposit must still be handled, not merely made rarer. Mirrors TryDeposit's
        /// own refusal set and EXACT wording, so a player never sees two different messages for one
        /// cause: store == null, store not loaded, VaultAccess.None, UseBackpackSlot, at-capacity. Does
        /// NOT check "still parented" - unlike TryDeposit, CanAccept runs BEFORE detachment, so the item
        /// being parented to the player here is normal, not a refusal condition.
        ///
        /// Also satisfies DESIGN section 10: VerifySellItems is named as one of the two places
        /// authorization is re-resolved per transaction, and consulting TryGetAccess from CanAcceptCore
        /// is what makes that true. The caller is wired at Player_Commerce.cs:530. Note this is a
        /// PRE-FLIGHT and is TOCTOU by construction - a second window on the same store can fill it
        /// between this check and TryDeposit, which is R3's whole premise - so the store's own
        /// unconditional check is the enforcing one and neither may be removed on the grounds that the
        /// other exists.
        /// </summary>
        public override bool CanAccept(WorldObject wo, Player player, out string reason)
        {
            return CanAcceptCore(wo, VaultActor.From(player), out reason);
        }

        /// <summary>
        /// One TryGetAccess per transaction instead of one per item. TryGetAccess short-circuits only
        /// for the OWNING account, so for every grantee and every unauthorized stranger it reaches
        /// backend.GetAccountVaultGrants - a fresh ShardDbContext and a synchronous SELECT, on the
        /// world thread. Resolving per item made a repeated-guid Sell packet one query per repetition.
        ///
        /// Safe to hoist because CanAccept is a preflight, not the enforcing gate. A grant CAN change
        /// while the loop runs: grant mutations serialize against each other on the store's mutation
        /// queue, but the loop does not run on that queue - Enqueue runs queued work on the CALLING
        /// thread (it calls Drain itself at AccountVaultStore.cs:579, and runs the work inline at
        /// :557-561 when the caller is already on the queue) and landblock groups tick in parallel
        /// (LandblockManager.cs:363). What makes the wider window harmless is that
        /// AccountVaultStore.TryDeposit re-resolves access on the queue inside every actual deposit
        /// (AccountVaultStore.cs:1018, refusing at :1021-1025) and hands the item back on refusal, so a
        /// revoke that lands mid-transaction is still honoured. The hoist only changes how early a
        /// doomed item is told no. The pre-hoist code had the same race, one iteration wide.
        /// </summary>
        public override bool TryResolveSellAccess(Player player, out VaultAccess resolvedAccess, out string failReason)
        {
            resolvedAccess = VaultAccess.None;
            failReason = null;

            var activeStore = Store;

            if (activeStore == null)
            {
                failReason = "Your vault is temporarily unavailable.";
                return false;
            }

            return activeStore.TryGetAccess(VaultActor.From(player), out resolvedAccess, out failReason);
        }

        public override bool CanAccept(WorldObject wo, Player player, VaultAccess resolvedAccess, out string reason)
        {
            return CanAcceptCore(wo, VaultActor.From(player), resolvedAccess, out reason);
        }

        /// <summary>
        /// The testable core of <see cref="CanAccept"/>: takes a <see cref="VaultActor"/> directly so
        /// it can be exercised without a live Player (see PersonalVendorTests.cs).
        ///
        /// Fix round 2 (F1, F2, F4): every branch below now calls the same primitive TryDeposit itself
        /// uses, rather than a collapsed public wrapper or a re-derived condition, so this cannot drift
        /// from TryDeposit's own refusal set a second time:
        ///  - readiness goes through <see cref="AccountVaultStore.TryCheckReady"/>, which - unlike
        ///    <see cref="AccountVaultStore.IsLoaded"/> - distinguishes an index-load FAILURE
        ///    (UnavailableMessage) from vaults still finishing their async load (StillLoadingMessage);
        ///  - access goes through <see cref="AccountVaultStore.TryGetAccess"/>, which - unlike
        ///    a bare bool-discarding call - distinguishes a grant-read
        ///    FAILURE (UnavailableMessage) from a genuine "no grant" (the permission string);
        ///  - the pack-slot message is TryDeposit's own ternary on WeenieType == Container, copied
        ///    verbatim, because a Focusing Stone (RequiresPackSlot true, not a Container) occupying a
        ///    pack slot without being a pack cannot act on "store what is inside it";
        ///  - the capacity check is gated by <see cref="AccountVaultStore.WouldAddEntry"/> exactly as
        ///    TryDeposit's own cap check is, so a pristine item topping up an already-held ledger row
        ///    is never refused for a cap it would not actually consume (DESIGN 7.3).
        /// </summary>
        internal bool CanAcceptCore(WorldObject wo, VaultActor actor, out string reason)
        {
            var store = Store;

            if (store == null)
            {
                reason = AccountVaultStore.UnavailableMessage;
                return false;
            }

            if (!store.TryCheckReady(out reason))
                return false;

            if (!store.TryGetAccess(actor, out var access, out reason))
                return false;

            return CanAcceptCore(wo, actor, access, out reason);
        }

        /// <summary>
        /// The same core, given access the caller has ALREADY resolved through
        /// <see cref="AccountVaultStore.TryGetAccess"/> - see <see cref="TryResolveSellAccess"/> for why
        /// the sell path resolves it once per transaction rather than once per item. Every gate below
        /// the access test is unchanged and still runs here, so the two entry points differ only in
        /// where the VaultAccess came from.
        ///
        /// The store-null and readiness checks are repeated rather than skipped: they are cheap, they
        /// are not what the hoist is avoiding (neither touches the shard), and repeating them keeps this
        /// overload safe to call directly rather than only as the tail of the resolving one.
        ///
        /// <paramref name="actor"/> is no longer read here - access is the only thing it was used for -
        /// but it stays in the signature so the two overloads remain a matched pair and a future gate
        /// that needs the actor has it.
        /// </summary>
        internal bool CanAcceptCore(WorldObject wo, VaultActor actor, VaultAccess access, out string reason)
        {
            var store = Store;

            if (store == null)
            {
                reason = AccountVaultStore.UnavailableMessage;
                return false;
            }

            if (!store.TryCheckReady(out reason))
                return false;

            if (access == VaultAccess.None)
            {
                reason = "You do not have permission to use this vault.";
                return false;
            }

            if (wo != null && wo.UseBackpackSlot)
            {
                reason = wo.WeenieType == WeenieType.Container
                    ? "A pack cannot be stored in your vault. Store what is inside it instead."
                    : "That takes up a pack slot of its own and cannot be stored in your vault.";
                return false;
            }

            // Fix round 2, F3: the pre-flight half of AccountVaultStore.TryDeposit's timed-item
            // refusal, with the SAME message - see that method for why a Lifespan-bearing item cannot
            // go into the vault (collapse ignores CreationTimestamp, so a withdrawal would restart the
            // clock). This must stay in step with the store's own guard: without it, a timed item is
            // detached from the player's pack and flushed to the shard before the store refuses it,
            // and the player is told nothing until the hand-back.
            if (wo != null && wo.Lifespan != null)
            {
                reason = "A timed item cannot be stored in your vault.";
                return false;
            }

            // NO trade-note refusal here, and that omission is deliberate. ItemType.PromissoryNote is
            // ACCEPTED by this pre-flight so the note reaches the sell list; DepositItems then diverts
            // it to the player's bank (Player.BankTradeNote) instead of handing it to the store, which
            // is why it never becomes vault content and never has to satisfy any storage rule. The
            // matching guard in AccountVaultStore.TryDeposit is therefore the ONE store refusal not
            // mirrored here - see that method's own remark.

            // store.EffectiveEntryCap, not the static AccountVaultStore.BaseEntryCap: the sell-list
            // pre-flight has to refuse on exactly the number TryDeposit enforces, or an account that
            // bought AugmentationMuleSpace is turned away here for room the store would have given it.
            //
            // HasObviousHeadroom is a COST gate in front of that check, not a second rule. WouldAddEntry
            // reaches VaultCollapse.IsPristine, which constructs a reference weenie, computes its object
            // description and destroys it - and AccountVaultStore.TryDeposit then calls IsPristine AGAIN
            // for the same item, because it needs the answer for real. Paying for it twice per item is
            // pure waste whenever the cap cannot refuse, which is whenever one more entry provably fits.
            // At the boundary the full check still runs, unchanged.
            //
            // The refusal itself needs EntryCount, so it is read ONCE into a local rather than twice
            // (the test and the message used to take the lock and recount the grouping separately, and
            // could in principle print a different number from the one they refused on).
            if (wo != null && !store.HasObviousHeadroom())
            {
                var entryCount = store.EntryCount;

                if (store.WouldAddEntry(wo) && entryCount >= store.EffectiveEntryCap)
                {
                    reason = $"Your vault is full: {entryCount} of {store.EffectiveEntryCap} entries. Withdraw something before storing more.";
                    return false;
                }
            }

            reason = null;
            return true;
        }

        /// <summary>
        /// Fix round 1, I5: gate READS the same way gate 2 gates transactions, and it lives HERE rather
        /// than only at the Player_Commerce call sites, so a Player_Commerce edit cannot silently drop
        /// it. Player_Commerce.HandleActionBuyItem calls this on the FALSE branch of
        /// BuyItems_ValidateTransaction (VendorType.Undef, to refresh the alt-currency figure after a
        /// rejected purchase) - without a gate here, a stranger whose transaction gate 2 correctly
        /// refused would still have the ENTIRE vault listing serialized to them via RebuildView plus
        /// base.ApproachVendor, and player.LastOpenedContainerId set, making every stored item resolve
        /// through FindObject(..., SearchLocations.LastUsedContainer). Gate 2 held; this closes the read
        /// side, which had no gate at all.
        ///
        /// An unauthorized call leaves DefaultItemsForSale and storeItems exactly as they were -
        /// RebuildView is not even reached, so nothing is (re)built for a player who should not see it.
        /// </summary>
        public override void ApproachVendor(Player player, VendorType action = VendorType.Undef, uint altCurrencySpent = 0)
        {
            if (player == null)
                return;

            // Whatever the outcome below, this is the approach a deferred refresh was waiting to send
            // (or one that would be refused the same way), so it satisfies any pending for this player.
            CancelPendingRefresh(player);

            if (!TryPrepareApproach(VaultActor.From(player), out var failReason))
            {
                player.SendTransientError(failReason);
                return;
            }

            base.ApproachVendor(player, action, altCurrencySpent);

            SendClientOnlyValueOverrideForZeroValueItems(player);
        }

        /// <summary>
        /// Client-only, unpersisted workaround for a client-side drag refusal, not a gameplay change.
        /// The AC client will not let a player drag an item into the vendor SELL pane if the item's
        /// Value is 0 - and the server only sets the WeenieHeaderFlag.Value bit (so only sends a Value
        /// at all) when Value is greater than 0 (WorldObject_Networking.cs:751, `if (Value != null &&
        /// (Value > 0))`), so a genuinely Value-0 item's header carries no Value property for the client
        /// to read as nonzero in the first place. Several rares (e.g. Lugian's Pearl, wcid 30240) ship
        /// with Value 0 and are otherwise sellable here: a PersonalVendor's sale price is always 0
        /// (DESIGN section 5/9) and Player_Commerce.IsAcceptableToSell already special-cases
        /// VendorType.PersonalVendor to bypass the ordinary Value&lt;1 "unsellable" rejection
        /// (Player_Commerce.cs:459) - the client is the only remaining obstacle for these items.
        ///
        /// This sends each such item's owning player a PublicUpdatePropertyInt(item, Value, 1) purely
        /// to unstick the client's local drag check. It must be the PUBLIC variant: the Private one
        /// carries no object guid (GameMessagePrivateUpdatePropertyInt writes sequence, property,
        /// value only), so the client applies it to the PLAYER's own property table and the item is
        /// never touched. The first version of this fix used Private and did nothing visible in-game;
        /// /setproperty PropertyInt.Value 1 on the gem, which goes through Player.UpdateProperty and
        /// the Public message, was what actually unstuck the drag. Sending only to the owning
        /// session keeps it private in effect. It never calls SetProperty or touches the biota:
        /// nothing is persisted, ace_shard is untouched, and Player_Commerce still reads the item's
        /// real (0) Value for every server-side accept/price decision.
        ///
        /// Accepted trade-off: until the player relogs (or the item's true Value is otherwise
        /// re-synced), the client also believes these items are draggable into an ORDINARY town
        /// vendor's sell pane. Player_Commerce.IsAcceptableToSell there still enforces Value&gt;=1 for a
        /// non-PersonalVendor and will refuse the sale with "unsellable" - a rejected message, not a
        /// lost item, and the item is simply returned to the player's inventory. No revert is sent on
        /// mule close; the override is harmless left in place, and a relog restores the true value to
        /// the client either way.
        /// </summary>
        private void SendClientOnlyValueOverrideForZeroValueItems(Player player)
        {
            if (player.Session == null)
                return;

            var messages = BuildZeroValueOverrideMessages(player.GetAllPossessions());

            if (messages.Count > 0)
                player.Session.Network.EnqueueSend(messages);
        }

        /// <summary>
        /// Message-building half of <see cref="SendClientOnlyValueOverrideForZeroValueItems"/>, split
        /// out so the MESSAGE TYPE is testable without a live Player.Session. The first version of this
        /// fix built GameMessagePrivateUpdatePropertyInt here, which carries no object guid, and no test
        /// could catch it; PersonalVendorTests now asserts every message is the Public opcode and names
        /// the item's guid.
        /// </summary>
        internal static List<GameMessage> BuildZeroValueOverrideMessages(IEnumerable<WorldObject> possessions)
        {
            var messages = new List<GameMessage>();

            foreach (var item in SelectZeroValueItems(possessions))
                messages.Add(new GameMessagePublicUpdatePropertyInt(item, PropertyInt.Value, 1));

            return messages;
        }

        /// <summary>
        /// Selection half of <see cref="SendClientOnlyValueOverrideForZeroValueItems"/>, split out so it
        /// is testable without a live Player (that method needs a real Player.Session to send network
        /// messages, so it cannot run in this test project - see PersonalVendorTests.cs class remarks).
        /// An item qualifies when its Value is null, zero, or negative.
        /// </summary>
        internal static IEnumerable<WorldObject> SelectZeroValueItems(IEnumerable<WorldObject> possessions)
        {
            return possessions.Where(i => (i.Value ?? 0) <= 0);
        }

        /// <summary>
        /// The testable core of the gated half of <see cref="ApproachVendor"/>: authorization, the R10
        /// Home default, and rebuilding the view - everything except the final
        /// base.ApproachVendor(player, ...) call, which needs a live Player.Session to send a network
        /// message and so cannot run in this test project (see PersonalVendorTests.cs class remarks).
        /// Returns false, having touched neither Home nor the view, when the actor is unauthorized -
        /// this is what lets a test prove I5's gate without a live Player.
        /// </summary>
        internal bool TryPrepareApproach(VaultActor actor, out string failReason)
        {
            if (!TryAuthorize(actor, out _, out failReason))
                return false;

            // R10, belt and braces alongside PrepareResetToHome below: a hand-spawned vendor with no
            // Home set yet gets one here rather than relying solely on the summon path remembering to
            // set it before EnterWorld.
            Home = Home ?? Location;

            RebuildView();
            RestampRot();

            return true;
        }

        /// <summary>
        /// Fix round 1, I6: CheckResetToHome (Vendor.cs:387, `Location.Pos.Equals(Home.Pos)`) is
        /// NON-VIRTUAL, so a subclass cannot guard the dereference itself - PrepareResetToHome IS
        /// virtual, specifically so a subclass can stop it from ever being scheduled in the first
        /// place (Task 6 virtualized it for exactly this class). A mule has no home to walk back to -
        /// its lifetime is the distance leash and TimeToRot (DESIGN 11.4), not a reset - so this is
        /// inert rather than merely relying on Home being set: R10 becomes UNREACHABLE here, not merely
        /// safe if reached. The `Home = Home ?? Location;` line above is redundant with this override in
        /// the sense that neither alone is the whole mitigation - this override means CheckResetToHome
        /// is never scheduled at all, and the Home assignment is what keeps a hand-built vendor safe
        /// even if some future edit calls CheckResetToHome, PrepareResetToHome, or base.ApproachVendor
        /// (which still calls PrepareResetToHome, just onto this inert override) through some path that
        /// does not go through this class's own ApproachVendor override.
        /// </summary>
        protected override void PrepareResetToHome()
        {
        }

        /// <summary>
        /// DESIGN 11.4: "TimeToRot refreshed on every interaction". Fix round 2 (coordinator addendum
        /// after the summon task landed in 34c471648): reads the same
        /// account_vault_summon_rot_seconds config key MuleSummonHandler.TrySummon stamps at summon
        /// time (MuleSummonHandler.cs, right after TryPlaceVendor succeeds), so a mid-life re-stamp can
        /// never silently diverge from the summon-time lifetime - one registered default
        /// (PropertyManager.cs's DefaultLongProperties entry for this key), read from both places. The
        /// placeholder constant this used to hold is deleted; nothing else in this feature referenced
        /// it.
        ///
        /// Clamped toward a SAFE SHORT lifetime, not passed through unbounded: WorldObject_Decay.cs
        /// treats a TimeToRot of exactly -1 as "Never Rot" (WorldObject_Decay.cs:42), so a
        /// misconfigured non-positive value here would make an abandoned mule immortal rather than
        /// short-lived if passed straight through - the wrong failure direction for an object that
        /// also permanently pins its store's OpenWindows count for as long as it lives (see the class
        /// remarks on the still-open I8/RemoveWindow-on-destroy gap, now the summon task's). A
        /// non-positive configured value falls back to <see cref="MinimumRotSecondsOnMisconfiguration"/>
        /// instead.
        ///
        /// PropertyManager.GetLong reads its process-static cache once populated, not the database, on
        /// every subsequent call for a key (PropertyManager.cs's ModifyLong/GetLong) - and
        /// MuleSummonHandler.TrySummon already performs this exact call, synchronously, in the
        /// directly-analogous "handling one player's interaction with this vendor" context this method
        /// runs in (ApproachVendor -> TryPrepareApproach -> RestampRot), which is what already
        /// guarantees the key is cached by the time any live vendor's ApproachVendor can run this. This
        /// is therefore a cache read here, not a live database call on this thread - the same
        /// PropertyManager.GetLong NRE trap that bit this build's own test-constructibility work twice
        /// (see EnsureVendorConstructible's account_vault_entry_cap/account_vault_landblock seeding) is
        /// a cold-cache problem, and the summon path is what keeps this one warm.
        /// </summary>
        internal void RestampRot()
        {
            var configured = PropertyManager.GetLong("account_vault_summon_rot_seconds").Item;

            TimeToRot = configured > 0 ? configured : MinimumRotSecondsOnMisconfiguration;
        }

        /// <summary>Fallback lifetime, in seconds, when account_vault_summon_rot_seconds resolves to a
        /// non-positive value. Short and safe, never "never rot" (see RestampRot's remarks on -1).</summary>
        private const double MinimumRotSecondsOnMisconfiguration = 60;

        /// <summary>
        /// True when this window's cached view no longer matches the store it looks onto - that is,
        /// when <see cref="RebuildView"/> would rebuild rather than take its cache hit on the version
        /// stamp.
        ///
        /// Player_Commerce.HandleActionBuyItem consults this on the REFUSED-buy branch. For a
        /// PersonalVendor, BuyItems_ValidateTransaction IS <see cref="TryWithdrawTransaction"/>, so
        /// false there means the withdrawal was refused, not that the client sent junk - and one way it
        /// is refused is that another window on the same store already drained the row this panel is
        /// still showing. Re-approaching unconditionally is what Task 12 removed, because
        /// <see cref="ApproachVendor"/> costs a grants SELECT for a non-owner and the rejected-buy path
        /// can be looped for free; re-approaching only when the version has actually moved keeps that
        /// saving for every refusal that changed nothing, while a panel that has genuinely gone stale
        /// still self-corrects instead of needing the player to walk out of range and back.
        ///
        /// <see cref="AccountVaultStore.Version"/> is safe to read from here, and reading it cannot
        /// itself be the stale read this property exists to detect: it is written with
        /// Interlocked.Increment and read with Volatile.Read, and <see cref="RebuildView"/> stamps
        /// builtFromVersion BEFORE it reads the store's contents - so the stamp can only ever be BEHIND
        /// the state it was built from, never ahead of it. This property can therefore report a fresh
        /// view as stale, which costs one extra rebuild, and can never report a stale view as fresh.
        ///
        /// It deliberately says nothing about the empty-view case that RebuildView's own
        /// `Count > 0` clause covers. A window with no rows gives the client nothing to name in a buy
        /// request, so a refused buy cannot reach here against one through the panel.
        /// </summary>
        internal bool ViewIsStale
        {
            get
            {
                var activeStore = Store;

                return activeStore != null && builtFromVersion != activeStore.Version;
            }
        }

        /// <summary>
        /// Brings <see cref="storeItems"/>, DefaultItemsForSale and the proxy/class lookup maps up to
        /// date with the store's current contents.
        ///
        /// STABLE DISPLAY GUIDS. A display row whose identity (<see cref="DisplayKey"/>) and rendered
        /// inputs (<see cref="DisplaySig"/>) are unchanged since the previous build KEEPS its display
        /// object - same instance, same guid - and is not re-materialized. Only rows that were added,
        /// removed or changed create or destroy objects. A changed row is REPLACED, never edited in
        /// place: a display object's rendered state never changes after it is built. Every snapshot
        /// display nothing in the new view claims is destroyed - they are disposable
        /// materializations, never persisted (biotaOriginatedFromDatabase is false for a freshly-created
        /// one), which matches retail's own "buying a default item creates a fresh one" semantic
        /// (DESIGN 9.1). storeItems entries are NEVER destroyed here - they are live references into
        /// the store's own vault containers.
        ///
        /// What is NEVER carried over is the lookup side: storeItems, proxiedItems and classDisplays are
        /// cleared and refilled from THIS pass's entries only, so a kept guid always resolves to its
        /// row's current members and current count, never to the ones it was first built for.
        ///
        /// AccountVaultStore.GetEntries already refuses - returns an empty list, never a partial one -
        /// when the store or any one of its vaults has not finished its async load (R4), so an
        /// unloaded store simply leaves this window empty rather than throwing. The first touch of a
        /// cold store synchronously reads the shard database (AccountVaultStore.TryEnsureLoadedLocked)
        /// on whatever thread calls in here; this method does not itself dispatch that off the world
        /// tick thread. Warming the store before summoning what will call this is the summon path's
        /// responsibility (a later task), not this one's.
        /// </summary>
        internal void RebuildView()
        {
            var store = Store;

            // The cheap exit, and it has to come before anything is destroyed. F7: this method runs on
            // EVERY approach, and Player_Commerce's rejected-buy path re-approached with no IsBusy
            // check and no cooldown, so a loop of deliberately invalid buy profiles was a free rebuild
            // treadmill - up to account_vault_entry_cap fresh display objects each pass, each one a
            // dynamic guid that GuidManager will not reissue for 360 minutes.
            //
            // The count clause is the escape hatch for the one state change the version counter cannot
            // see. AccountVaultStore.GetEntries returns an EMPTY list - never a partial one - until the
            // vault index and every vault inventory have finished loading, and finishing that load is
            // not a mutation and bumps nothing. Always rebuilding an empty view means a store that was
            // still loading on the first approach fills in on the next one instead of caching nothing
            // forever.
            if (store != null && builtFromVersion == store.Version && (DefaultItemsForSale.Count + storeItems.Count > 0 || builtFromLoadedStore))
                return;

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            // Snapshot BEFORE anything is cleared: these are the only objects this pass may reuse, and
            // every one of them that is not claimed below is destroyed in the finally.
            var snapshot = new Dictionary<ObjectGuid, WorldObject>(DefaultItemsForSale);
            var oldRecords = displayRecords;
            var newRecords = new Dictionary<DisplayKey, DisplayRecord>();
            var claimed = new HashSet<ObjectGuid>();

            DefaultItemsForSale.Clear();
            storeItems.Clear();
            proxiedItems.Clear();
            classDisplays.Clear();
            sortNames.Clear();

            LastRebuildCreated = 0;
            LastRebuildReused = 0;
            LastRebuildDestroyed = 0;

            try
            {
                if (store == null)
                {
                    builtFromVersion = -1;
                    return;
                }

                // Stamped BEFORE the contents are read, and that ordering is the whole reason the version
                // can be read without a lock - see AccountVaultStore.Version. A mutation landing between
                // this line and the loop below can only make the stamp one BEHIND the state actually built
                // from, which costs an extra rebuild later; it can never stamp a view as newer than it is.
                builtFromVersion = store.Version;

                // Read alongside the version stamp above, for the SAME reason that stamp is read before
                // the contents are: a mutation (or a load finishing) landing between this line and the loop
                // below can only make this snapshot stale in the safe direction (forcing an extra rebuild
                // next time), never stamp a still-loading build as loaded.
                builtFromLoadedStore = store.IsLoaded;

                var filter = SearchFilter;

                LastRebuildShown = 0;
                LastRebuildTotal = 0;

                foreach (var entry in store.GetEntries(0, -1))
                {
                    LastRebuildTotal++;

                    // Filtering happens BEFORE any display object is created - a skipped entry never burns a
                    // dynamic guid. The match rule itself lives in VaultSearch.Matches, shared with
                    // /mule search all's chat report so the panel and the report cannot disagree.
                    if (filter != null && !VaultSearch.Matches(filter, entry, DatabaseManager.World.GetCachedWeenie))
                        continue;

                    LastRebuildShown++;

                    if (entry.Kind == VaultEntryKind.Class)
                    {
                        var key = new DisplayKey(DisplayKind.Class, 0, entry.ClassDisplayId);
                        var sig = new DisplaySig(entry.Wcid, entry.Count, entry.TotalValue, entry.CanonicalForm);

                        var classDisplay = ClaimOrBuild(key, sig, oldRecords, snapshot, claimed, newRecords, out var baseName, (out string builtBaseName) => BuildClassDisplay(store, entry, out builtBaseName));

                        if (classDisplay == null)
                            continue;

                        if (baseName != null)
                            RegisterSortName(classDisplay.Guid, baseName);

                        DefaultItemsForSale[classDisplay.Guid] = classDisplay;

                        // THIS pass's entry, never the one the kept object was first built for - see
                        // this method's remarks on why the lookup side is never carried over.
                        classDisplays[classDisplay.Guid] = entry;

                        continue;
                    }

                    if (entry.Kind == VaultEntryKind.Ledger)
                    {
                        var key = new DisplayKey(DisplayKind.Ledger, entry.Wcid, null);
                        var sig = new DisplaySig(entry.Wcid, entry.Count);

                        var display = ClaimOrBuild(key, sig, oldRecords, snapshot, claimed, newRecords, out var baseName, (out string builtBaseName) => BuildLedgerDisplay(entry, out builtBaseName));

                        if (display == null)
                            continue;

                        if (baseName != null)
                            RegisterSortName(display.Guid, baseName);

                        DefaultItemsForSale[display.Guid] = display;
                    }
                    else if (entry.WorldObject != null)
                    {
                        // A stored item the panel would refuse to draw is presented through a proxy
                        // instead, and is deliberately NOT also added to storeItems - one item, one row.
                        // A proxy that cannot be built falls through to the ordinary path, which leaves
                        // the item exactly as invisible as it is today rather than losing it: the vault
                        // still holds it, and the failure is logged rather than silent.
                        //
                        // A GROUP entry travels the same path: the proxy is built from the representative
                        // and the whole membership is registered behind it. Grouping only ever applies to
                        // ItemType.TinkeringMaterial, which always needs a proxy, so the fallback below is
                        // reached for a group only when the proxy could not be materialized at all - and it
                        // then files EVERY member individually rather than only the representative, so a
                        // failed proxy costs the row its grouping, never its other members.
                        WorldObject proxy = null;
                        string baseName = null;

                        if (NeedsDisplayProxy(entry.WorldObject.ItemType))
                        {
                            var stored = entry.WorldObject;
                            var groupCount = entry.IsGroup ? entry.Count : 0;

                            var key = new DisplayKey(entry.IsGroup ? DisplayKind.ProxyGroup : DisplayKind.ProxyOne, stored.Guid.Full, null);
                            var sig = ProxySig(stored, groupCount);

                            proxy = ClaimOrBuild(key, sig, oldRecords, snapshot, claimed, newRecords, out baseName, (out string builtBaseName) => TryCreateDisplayProxy(stored, groupCount, out var built, out builtBaseName) ? built : null);
                        }

                        if (proxy != null)
                        {
                            if (baseName != null)
                                RegisterSortName(proxy.Guid, baseName);

                            DefaultItemsForSale[proxy.Guid] = proxy;

                            // THIS pass's membership, never the one the kept proxy was first built for.
                            proxiedItems[proxy.Guid] = entry.Members;
                        }
                        else
                        {
                            foreach (var member in entry.Members)
                                storeItems[member.Guid] = member;
                        }
                    }
                }
            }
            finally
            {
                // Every snapshot display nothing in the new view claimed is gone from the panel, and it
                // is disposable - destroy it. This runs through ANY exit, including the store-null
                // return and a throw part way through the loop, so a display object is never leaked
                // outside both DefaultItemsForSale and the records map.
                foreach (var (guid, display) in snapshot)
                {
                    if (claimed.Contains(guid))
                        continue;

                    if (!display.IsDestroyed)
                        display.Destroy();

                    LastRebuildDestroyed++;
                }

                displayRecords = newRecords;

                stopwatch.Stop();

                if (log.IsDebugEnabled)
                    log.Debug($"[MULE VENDOR] {Name}: rebuilt view for account {StoreAccountId} in {stopwatch.Elapsed.TotalMilliseconds:F2} ms - created {LastRebuildCreated}, reused {LastRebuildReused}, destroyed {LastRebuildDestroyed}, shown {LastRebuildShown} of {LastRebuildTotal}.");
            }
        }

        /// <summary>The display-object builder <see cref="ClaimOrBuild"/> falls back to. Returns null when nothing could be built.</summary>
        private delegate WorldObject DisplayBuilder(out string baseName);

        /// <summary>
        /// Returns the previous build's display object for <paramref name="key"/> when it may be kept,
        /// or builds a fresh one. KEPT only when ALL of these hold: the previous build recorded that
        /// key; with an equal signature, so the row renders exactly as it did; its object has not been
        /// destroyed (a vendor Destroy, for one, reaches every display); it was actually in
        /// DefaultItemsForSale when this pass began; and nothing earlier in THIS pass already claimed
        /// it - one display object backs at most one row per pass, so a duplicate key builds a second
        /// object rather than sharing the first.
        ///
        /// A kept row's Name is left exactly as it is - its count suffix was applied once, when it was
        /// built, and the signature proves the count has not moved - and its recorded base name is
        /// handed back so the caller can re-register it in <see cref="sortNames"/>.
        /// </summary>
        private WorldObject ClaimOrBuild(DisplayKey key, DisplaySig sig, Dictionary<DisplayKey, DisplayRecord> oldRecords, Dictionary<ObjectGuid, WorldObject> snapshot,
                                         HashSet<ObjectGuid> claimed, Dictionary<DisplayKey, DisplayRecord> newRecords, out string baseName, DisplayBuilder build)
        {
            if (oldRecords.TryGetValue(key, out var record)
                && Equals(record.Sig, sig)
                && !record.Display.IsDestroyed
                && snapshot.TryGetValue(record.Display.Guid, out var inSnapshot)
                && ReferenceEquals(inSnapshot, record.Display)
                && claimed.Add(record.Display.Guid))
            {
                newRecords.TryAdd(key, record);
                LastRebuildReused++;

                baseName = record.BaseName;
                return record.Display;
            }

            var display = build(out baseName);

            if (display == null)
                return null;

            LastRebuildCreated++;

            // TryAdd, not an indexer: on a duplicate key the FIRST row keeps the record. The second
            // row's object is simply unrecorded, so the next pass replaces it - correct, just not
            // stable, for a case the store should never produce.
            newRecords.TryAdd(key, new DisplayRecord(display, sig, baseName));

            return display;
        }

        /// <summary>
        /// Everything <see cref="TryCreateDisplayProxy"/> copies off the stored item, plus the ItemType
        /// that decided it needed a proxy and the group's member count. Read off the stored REPRESENTATIVE
        /// for a group - which is also what the proxy is built from.
        /// </summary>
        private static DisplaySig ProxySig(WorldObject stored, long groupCount)
        {
            return new DisplaySig(
                stored.WeenieClassId,
                groupCount,
                StackSize: stored.StackSize,
                Name: stored.Name,
                Value: stored.Value,
                MaterialType: (int?)stored.MaterialType,
                ItemWorkmanship: stored.ItemWorkmanship,
                NumItemsInMaterial: stored.NumItemsInMaterial,
                Structure: stored.Structure,
                MaxStructure: stored.MaxStructure,
                ItemType: (int)stored.ItemType);
        }

        /// <summary>
        /// Materializes the display object for one counted item-CLASS line. Null (logged) when the line
        /// cannot be drawn. <paramref name="baseName"/> is the pre-suffix Name when a count suffix was
        /// applied, otherwise null.
        /// </summary>
        private WorldObject BuildClassDisplay(AccountVaultStore store, VaultEntry entry, out string baseName)
        {
            baseName = null;

            // A class row is drawn from its OWN payload, not from a bare instance of its wcid:
            // the whole point of a class is the per-instance properties the template does not
            // carry, so a fresh wcid would draw "Salvage" where the player stored
            // "Salvage (73)". Per-item Value is the pooled share.
            //
            // ONE LINE MAY COVER SEVERAL account_vault_class ROWS, and this draws the
            // REPRESENTATIVE's payload deliberately: every member of a display group is
            // identical on the four things the panel shows (wcid, Structure, the rendered
            // workmanship quotient and Name), differing only where AreGroupable already
            // forgives. The pooled share below is therefore the GROUP's average, which can be a
            // pyreal or two off the share a particular withdrawn bag carries - the same
            // looseness a display group of loose items already has, since Value is a groupable
            // key there too. The whole entry travels into classDisplays, so a withdraw against
            // this line drains its members exactly.
            if (!VaultItemClass.TryParseCanonicalForm(entry.CanonicalForm, out var classWcid, out var classOverrides, out _)
                || classWcid != entry.Wcid)
            {
                log.Error($"[MULE VENDOR] {Name}: could not read back the canonical form of class {entry.ClassKey} (account {StoreAccountId}, wcid {entry.Wcid}); that row will not appear in this window. The items are NOT lost - the row is untouched and an operator can inspect account_vault_class.");
                return null;
            }

            // Through the store's world seam rather than VaultItemClass directly, matching every
            // other object this window builds: it is still the ONE materializer, reached the
            // one way a test can also reach it.
            var classDisplay = store.World.MaterializeClass(entry.Wcid, classOverrides, AccountVaultStore.PooledShare(entry.TotalValue, entry.Count));

            if (classDisplay == null)
            {
                log.Error($"[MULE VENDOR] {Name}: could not materialize a display object for class {entry.ClassKey} (wcid {entry.Wcid}, account {StoreAccountId}); that row will not appear in this window.");
                return null;
            }

            classDisplay.ContainerId = Guid.Full;

            // Same reason as the ledger branch: salvage is ItemType.TinkeringMaterial,
            // which the client's vendor panel refuses to file, and a class row is by
            // construction always TinkeringMaterial.
            StampForDisplay(classDisplay);

            classDisplay.CalculateObjDesc();

            // A class row is always indivisible items (TryDescribeClass refuses anything
            // stacked), so there is no stack size to set and the label rule is the
            // non-stackable one: number it only when the row holds more than one.
            if (entry.Count > 1)
            {
                baseName = classDisplay.Name;
                classDisplay.Name = $"{baseName} ({entry.Count:N0} in vault)";
            }

            classDisplay.VendorShopCreateListStackSize = -1;

            return classDisplay;
        }

        /// <summary>
        /// Materializes the display object for one collapsed LEDGER row. Null (logged) when the wcid
        /// cannot be instantiated. <paramref name="baseName"/> is the pre-suffix Name when a count
        /// suffix was applied, otherwise null.
        /// </summary>
        private WorldObject BuildLedgerDisplay(VaultEntry entry, out string baseName)
        {
            baseName = null;

            var display = WorldObjectFactory.CreateNewWorldObject(entry.Wcid);

            if (display == null)
            {
                log.Error($"[MULE VENDOR] {Name}: could not materialize a display object for ledger wcid {entry.Wcid} (account {StoreAccountId}); that stack will not appear in this window.");
                return null;
            }

            display.ContainerId = Guid.Full;

            // A ledger row is materialized from the same wcid the stack collapsed from, so it
            // inherits that wcid's ItemType and is just as undrawable as a stored one would be
            // if the type is a hidden one. Pristine Full Bags of salvage collapse into exactly
            // this path, so stamping only the stored branch would leave the commonest
            // salvage case still invisible.
            StampForDisplay(display);

            display.CalculateObjDesc();

            var maxStackSize = display.MaxStackSize ?? 0;

            // What the client will actually SHOW on this row. A stackable is capped at
            // MaxStackSize; a non-stackable renders as a single item however many the ledger
            // holds, because SetStackSize is skipped entirely for it.
            var shownCount = maxStackSize > 0 ? Math.Min(entry.Count, maxStackSize) : 1L;

            if (maxStackSize > 0)
                display.SetStackSize((int)shownCount);

            // The shown number is not the amount held whenever the ledger holds more than one
            // row's worth. A live test found the gap the hard way: 295 mana scarabs presented as
            // a stack of 100, two withdrawals of 100 succeeded, and the row still read 100 with
            // 95 actually left - there was no way short of trial and error to learn how many
            // remained. VendorShopCreateListStackSize = -1 lets the panel REQUEST any amount;
            // nothing was telling the player which amount to ask for.
            //
            // The true count goes in the NAME because that is the one field of a vendor row this
            // server fully owns and the client renders verbatim. The count is part of the row's
            // DisplaySig, so a row whose count moved is REPLACED by a freshly labelled object on
            // the next RebuildView, which is how the label tracks down as the stack is drawn out.
            //
            // EVERY STACKABLE row is labelled, whatever it holds, and the reason is
            // VendorShopCreateListStackSize = -1 a few lines below: it lets the buy panel request
            // ANY amount of this row, so the panel always looks willing to sell up to the cap no
            // matter how little is actually stored. The number the player needs is therefore
            // missing on every stackable row, not merely on the ones the cap truncates.
            //
            // Two narrower rules were tried first and both failed in play, which is why this one
            // is written down rather than trimmed again later. Labelling only CAPPED rows left
            // 8,993 Prismatic Tapers (MaxStackSize 10,000) and 98 Silver Scarabs (cap 100) bare
            // beside a labelled "Mana Scarab (295 in vault)", and the bare rows read as broken.
            // Labelling only counts above one still left a stackable holding exactly one looking
            // like it could sell a hundred.
            //
            // A NON-STACKABLE is labelled only when more than one is held. One discrete item
            // needs no number and would otherwise read "Awfully OP Shield (1 in vault)". It is
            // not automatically quiet, though: the stack-ledger branch collapses on IsPristine, not on
            // stackability, so three identical plain daggers become one ledger row of 3 that the
            // client still draws as a single item - the row that needs the label most.
            if (maxStackSize > 0 || entry.Count > 1)
            {
                // Captured BEFORE the suffix is appended and handed back so the caller registers it
                // under the display's OWN guid, and so a kept row can re-register it on a later
                // pass - see sortNames' own doc comment for why the count must never be a sort key.
                baseName = display.Name;
                display.Name = $"{baseName} ({entry.Count:N0} in vault)";
            }

            // M5: without this, the client presents one MaxStackSize stack per approach, so a
            // 10,000-kit ledger row (the exact DESIGN 7.3 headline case) needs about 100
            // re-approaches to buy out. Vendor.AddDefaultItem does the same for a stackSize-less
            // add (Vendor.cs:177); -1 lets the buy panel request an arbitrary amount, which
            // TryAdjustAccountVaultStack already adjudicates against what the ledger actually
            // holds, so an over-request is refused there rather than accepted here.
            display.VendorShopCreateListStackSize = -1;

            return display;
        }

        /// <summary>
        /// Stamps a DISPOSABLE display object with a renderable ItemType when its own would keep the
        /// client from drawing it. Never call this on a stored biota: see
        /// <see cref="proxiedItems"/> for why changing a real item's ItemType breaks crafting.
        ///
        /// No-op for the overwhelming majority of items, which carry a type the panel already files.
        /// </summary>
        private static void StampForDisplay(WorldObject display)
        {
            if (NeedsDisplayProxy(display.ItemType))
                display.ItemType = ProxyDisplayItemType;
        }

        /// <summary>
        /// Materializes the display proxy that stands in for one stored biota of a hidden ItemType.
        ///
        /// The proxy is the same disposable shape as a ledger row's display object - a fresh
        /// WorldObject of the same wcid, parented to this vendor, kept by the next
        /// <see cref="RebuildView"/> while every field copied here is unchanged and replaced (destroyed
        /// and rebuilt) as soon as one moves, never persisted (biotaOriginatedFromDatabase is false for
        /// one of these) - so it costs a dynamic guid for the life of the row and nothing else. Anything
        /// added to the copied set below must be added to <see cref="ProxySig"/> too, or a change to it
        /// will leave a kept proxy showing the old value.
        ///
        /// The handful of properties copied across are the ones that make one instance of a salvage
        /// bag distinguishable from another in the panel and in its appraisal: a bag's identity is its
        /// material, its units and its workmanship, and a proxy built from the bare weenie would show
        /// none of them - a player looking at "Salvage (Tiger Eye)" with no units cannot tell which of
        /// three bags they are about to withdraw. This is a display shell, NOT a faithful clone, and
        /// deliberately so: cloning everything would mean tracking every property the appraisal panel
        /// can show, which is the sort of list that goes stale silently.
        ///
        /// <paramref name="groupCount"/> is 0 for an ordinary one-biota row and the MEMBER COUNT for a
        /// group row. A group is labelled and made divisible; a singleton is neither.
        /// </summary>
        private bool TryCreateDisplayProxy(WorldObject stored, long groupCount, out WorldObject proxy, out string baseName)
        {
            baseName = null;

            proxy = WorldObjectFactory.CreateNewWorldObject(stored.WeenieClassId);

            if (proxy == null)
            {
                log.Error($"[MULE VENDOR] {Name}: could not materialize a display proxy for stored item 0x{stored.Guid.Full:X8} ({stored.Name}, wcid {stored.WeenieClassId}, ItemType {stored.ItemType}); it will stay in the vault but the panel cannot show it.");
                return false;
            }

            proxy.ContainerId = Guid.Full;

            // The approach message writes VendorShopCreateListStackSize ?? StackSize ?? 1 as the row's
            // amount, and TryWithdrawTransaction refuses any request that is not the whole stored
            // stack. A proxy whose stack size did not match the item it stands for would therefore
            // present an amount the server then refuses - the client asking for exactly what it was
            // shown and being told no. Guarded on MaxStackSize the same way the ledger branch is,
            // because SetStackSize is not meaningful for a non-stackable.
            //
            // FIRST, and not merely for tidiness: SetStackSize RECOMPUTES Value and EncumbranceVal
            // from the weenie's per-unit figures, so it silently overwrites anything copied across
            // before it. Copying Value first left every proxy showing the weenie's unit price instead
            // of the stored item's own - caught by RebuildView_ProxyCarriesTheStoredInstancesIdentity.
            if ((proxy.MaxStackSize ?? 0) > 0)
                proxy.SetStackSize(stored.StackSize);

            proxy.Name = stored.Name;
            proxy.Value = stored.Value;
            proxy.MaterialType = stored.MaterialType;
            proxy.ItemWorkmanship = stored.ItemWorkmanship;

            // Copied WITH ItemWorkmanship and not separable from it. WorldObject.Workmanship is
            // ItemWorkmanship / (NumItemsInMaterial ?? 1) (WorldObject_Properties.cs:1574-1603), so a
            // proxy that carried a real bag's ItemWorkmanship of 77 without its NumItemsInMaterial of
            // 12 computed 77, tripped that getter's out-of-range recovery branch, and had its OWN
            // ItemWorkmanship rewritten to 0. Observed in game 2026-08-31: the same bag read "Nearly
            // flawless (6.42)" and "Salvaged from 12 items" in the pack and "Workmanship: crafted (0)"
            // with no items line inside the vault. It is also what the grouping bucket key reads, so
            // grouping requires it too.
            proxy.NumItemsInMaterial = stored.NumItemsInMaterial;

            proxy.Structure = stored.Structure;
            proxy.MaxStructure = stored.MaxStructure;

            // A SINGLE stored biota is indivisible - it did not collapse into the ledger precisely
            // because it is not a plain identical-to-fresh stack - so its row is deliberately left
            // without VendorShopCreateListStackSize = -1: the only valid request is the whole thing.
            //
            // A GROUP of N is a different object. It is N whole indivisible biotas on one line, so the
            // panel legitimately needs to request an arbitrary k of them, and the count has to reach
            // the player somehow. Both halves mirror what the ledger rows already do a few lines above:
            // -1 lets the panel ask for any k, and the true N goes in the NAME because that is the one
            // field of a vendor row this server fully owns and the client renders verbatim.
            if (groupCount > 1)
            {
                // Captured BEFORE the suffix is appended and handed back so RebuildView registers it
                // (and re-registers it for a kept proxy) - see sortNames' own doc comment for why the
                // count must never be a sort key.
                baseName = proxy.Name;
                proxy.Name = $"{baseName} ({groupCount:N0} in vault)";
                proxy.VendorShopCreateListStackSize = -1;
            }

            StampForDisplay(proxy);

            proxy.CalculateObjDesc();

            return true;
        }

        #endregion
    }
}
