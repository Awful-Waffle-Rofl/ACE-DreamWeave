using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

using log4net;

using ACE.Common;
using ACE.Common.Extensions;
using ACE.Database;
using ACE.Database.Models.Shard;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

// See the note on IAccountVaultBackend.cs: the EF entity ACE.Database.Models.Shard.AccountVault
// cannot be named unqualified from inside the namespace ACE.Server.Entity.AccountVault.
using ShardAccountVault = ACE.Database.Models.Shard.AccountVault;

// THE BIOTA TRAP. AccountVaultSpawnFilter at the bottom of this file works on the EF entity
// ACE.Database.Models.Shard.Biota - navigation collections, the shape GetDynamicObjectsByLandblock
// returns - and NEVER on the runtime ACE.Entity.Models.Biota with its property dictionaries. Nothing
// in this file converts between the two.
using ShardBiota = ACE.Database.Models.Shard.Biota;

namespace ACE.Server.Entity.AccountVault
{
    /// <summary>
    /// One account's durable private item pool (DESIGN sections 5, 7, 7.1, 7.3 and 10).
    ///
    /// This type is the authoritative half of the Mule Vendor. It owns every mutation, every
    /// authorization decision and the capacity cap. A PersonalVendor is a disposable WINDOW onto a
    /// store and holds no authoritative state, and several windows may exist over one store at once -
    /// which is the case retail never faced, because retail vendor stock is per-vendor and in-memory.
    ///
    /// Nothing in this namespace may reference a vendor type. That boundary is what makes this class
    /// unit-testable without a network layer, and Task 7 depends on it holding.
    ///
    /// Contents live in one or more hidden <see cref="Container"/> vaults plus a collapsed-stackable
    /// ledger. An item provably identical to a fresh instance of its own weenie (see
    /// <see cref="VaultCollapse"/>) is destroyed and its units are added to the ledger; anything else
    /// keeps its biota and goes into a vault.
    ///
    /// Five invariants, each defending a named risk. None of them is stylistic:
    ///
    /// 1. <see cref="GetEntries(int, int)"/> is paged from the first line, even though v1 always asks
    ///    for the full range. Live test 17.1 measures the client vendor-panel ceiling and has not
    ///    returned; a paged-capable signature keeps that outcome a change to the vendor view and a
    ///    config default rather than a re-cut of this API and every caller (R8).
    ///
    /// 2. Vault capacity is pinned to <see cref="VaultItemCapacity"/> IN CODE and never read from
    ///    data. WorldObject.ItemCapacity is a (byte?) cast over PropertyInt.ItemsCapacity
    ///    (WorldObject_Properties.cs:1217) and the wire format is one byte, so a weenie value of 300
    ///    silently wraps to 44 and the vault quietly loses 211 slots with no error anywhere (R7).
    ///
    /// 3. <see cref="IsLoaded"/> is an AND across EVERY one of the account's vaults. The Container
    ///    constructor fires an ASYNC inventory load (Container.cs:89-96) and sets InventoryLoaded when
    ///    it finishes (Container.cs:183). A store with three vaults where two have finished would
    ///    otherwise report a short list and let a withdraw transact against a vault it cannot see
    ///    (R4).
    ///
    /// 4. Every mutation runs on this store's own single-threaded queue. The ledger's
    ///    ON DUPLICATE KEY UPDATE is the database-side half of R3; this queue is the in-memory half,
    ///    and it is what protects the biota path, which has no equivalent atomic statement.
    ///
    /// 5. A non-empty vault is never destroyed. Ever. See <see cref="TryReapEmptyVault"/> (R2).
    ///
    /// A read that FAILS is never read as a read that found nothing. The four vault reads on
    /// <see cref="IAccountVaultBackend"/> return null for a database failure and an empty list for
    /// "this account genuinely owns nothing", and conflating them would make a transient shard outage
    /// look like an empty vault - at which point insertion creates a fresh container, the player's
    /// items appear to have vanished, and the orphan can never be reaped because invariant 5 forbids
    /// destroying a container that might be non-empty.
    /// </summary>
    public class AccountVaultStore
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// Slots per vault container, pinned in code (R7, invariant 2 above). 255 is the largest value
        /// the one-byte wire format can carry, so this is simultaneously the maximum and the only safe
        /// value: anything above it wraps silently.
        /// </summary>
        public const int VaultItemCapacity = 255;

        /// <summary>
        /// The weenie a vault container is instantiated from.
        ///
        /// Deliberately an ORDINARY retail container (136, the plain backpack) rather than a new
        /// content unit. Nothing is read off the weenie: capacity is pinned above, the name is
        /// overwritten below, and the container never enters a landblock, is never sent to a client
        /// and is never used by a player. What the weenie must supply is only that it exists in every
        /// world database and that its WeenieType is Container, so a restart rehydrates the biota
        /// through the ordinary WorldObjectFactory path.
        /// </summary>
        public const uint VaultContainerWcid = (uint)WeenieClassName.W_BACKPACK_CLASS;

        private const string VaultContainerName = "Account Vault";

        private const string BarrelContainerName = "Account Barrel";

        /// <summary>R4: a vault whose async inventory load has not finished yet. Never proceed on partial data.</summary>
        public const string StillLoadingMessage = "Your vault is still loading, try again in a moment.";

        /// <summary>
        /// A read of the vault index, ledger or grant list FAILED. Distinct from "you own nothing" on
        /// purpose - see the class remarks.
        /// </summary>
        public const string UnavailableMessage = "Your vault is unavailable, try again in a moment.";

        private const string GoneMessage = "That is no longer in your vault.";

        /// <summary>
        /// The stable prefix of <see cref="TryDeposit"/>'s cap-refusal message (the count and cap
        /// numbers that follow vary per call, so a full-string match would never hit). A caller that
        /// needs to tell "vault is full" apart from every other deposit failure - see
        /// <see cref="Managers.Market.VaultMarketItemStore"/>, which must not tell a buyer to retry a
        /// refusal - should use <see cref="IsFullMessage"/> rather than matching this constant itself.
        /// </summary>
        private const string FullMessagePrefix = "Your vault is full:";

        /// <summary>
        /// True when <paramref name="failReason"/> is the cap-refusal <see cref="TryDeposit"/> returns
        /// when the destination account's vault has no room for another entry. Distinct from
        /// <see cref="StillLoadingMessage"/> and <see cref="UnavailableMessage"/>, both of which are
        /// transient and safe to retry - this one is not.
        /// </summary>
        public static bool IsFullMessage(string failReason)
            => failReason != null && failReason.StartsWith(FullMessagePrefix, StringComparison.Ordinal);

        /// <summary>
        /// How long a store may sit untouched before <see cref="AccountVaultManager"/> is allowed to
        /// drop it. A code constant rather than a config key because nothing about it is a gameplay
        /// decision: dropping a store only discards a cache over the shard, and the next access
        /// rebuilds it.
        /// </summary>
        internal const double IdleEvictionSeconds = 600.0;

        /// <summary>
        /// How long <see cref="SaveAndWait"/> waits for a biota save to be confirmed before calling it
        /// a failure. Reached only when creating a NEW vault container, which happens once per 255
        /// stored items, so the cost of the wait is not on the ordinary deposit path.
        ///
        /// Five seconds is a deadlock backstop, not a latency budget - it is still roughly 100x a
        /// normal save - but it is deliberately NOT generous, because the wait runs on the world tick
        /// thread (see <see cref="SaveAndWait"/>) and every player on that thread is frozen for its
        /// duration. A false timeout costs one container the caller rebuilds; a 30 second wait cost the
        /// whole tick.
        /// </summary>
        private const int VaultSaveTimeoutMs = 5000;

        /// <summary>
        /// Above this, <see cref="SaveAndWait"/> logs at ERROR. A save that blocks the world tick for
        /// even a fifth of a second is a visible stutter and must not be absorbed silently just because
        /// it eventually succeeded.
        /// </summary>
        private const long VaultSaveWarnElapsedMs = 200;

        private readonly IAccountVaultBackend backend;
        private readonly IAccountVaultWorldSource world;

        /// <summary>
        /// Guards <see cref="vaults"/>, <see cref="vaultRowIds"/>, <see cref="ledger"/> and
        /// <see cref="indexLoaded"/>. A leaf lock: nothing taken while holding it ever calls back into
        /// this store. Mutations already serialize on the mutation queue; this exists because READS
        /// (GetEntries, EntryCount, IsLoaded) legitimately arrive from other threads.
        /// </summary>
        private readonly object stateLock = new object();

        /// <summary>Oldest-first, matching the DAO's ORDER BY created_At, id. DESIGN 7.1 scans this order.</summary>
        private readonly List<Container> vaults = new List<Container>();

        /// <summary>container guid -> account_vault.id, so a reap can delete the right index row.</summary>
        private readonly Dictionary<uint, uint> vaultRowIds = new Dictionary<uint, uint>();

        /// <summary>
        /// The account's barrel container, or null when it has never barreled anything. Deliberately
        /// NOT a member of <see cref="vaults"/>: barreled items must not appear in GetEntries, must
        /// not count against capacity, and a barrel that will not load must not make the player's
        /// vault unusable. Every path that iterates vaults is therefore correct by construction, and
        /// the exclusion tests in AccountVaultBarrelTests are what keep it that way.
        ///
        /// Guarded by <see cref="stateLock"/> like <see cref="vaults"/> itself.
        /// </summary>
        private Container barrel;

        /// <summary>The account_vault row id of <see cref="barrel"/>, 0 when there is none.</summary>
        private uint barrelRowId;

        /// <summary>
        /// The account_vault.kind value of an ordinary vault container. 0 is the column default,
        /// which is what makes every row written before the barrel existed a vault with no backfill.
        /// </summary>
        internal const int VaultContainerKind = 0;

        /// <summary>The account_vault.kind value that marks a barrel container.</summary>
        internal const int BarrelContainerKind = 1;

        /// <summary>The refusal when the barrel cannot be reached. Distinct from the vault being unavailable.</summary>
        public const string BarrelUnavailableMessage = "The barrel is unavailable, try again in a moment.";

        /// <summary>
        /// The refusal when the entry the player asked to barrel carries an active market listing.
        /// Matched on by <see cref="Managers.Market.MarketError.ItemListed"/>'s classification, and by
        /// the tests, so the word "listed" is part of the contract rather than decoration.
        /// </summary>
        public const string ItemListedMessage = "That is listed on the market. Take the listing down before you barrel it.";

        /// <summary>
        /// How many VAULT containers this store holds - the barrel is excluded, because it is not one.
        /// Exists for the exclusion tests in AccountVaultBarrelTests: the failure this guards against
        /// is the barrel loading into <see cref="vaults"/>, which changes no signature and breaks no
        /// build.
        /// </summary>
        internal int VaultContainerCountForTest
        {
            get { lock (stateLock) return vaults.Count; }
        }

        /// <summary>
        /// Whether a barrel container is loaded. Exists for the exclusion tests, alongside
        /// <see cref="VaultContainerCountForTest"/>; production code asks the barrel for room instead.
        /// </summary>
        internal bool HasBarrelForTest
        {
            get { lock (stateLock) return barrel != null; }
        }

        /// <summary>wcid -> units held. Mirrors account_vault_stack; only non-zero rows are kept.</summary>
        private readonly Dictionary<uint, long> ledger = new Dictionary<uint, long>();

        private bool indexLoaded;

        /// <summary>
        /// The last read-time grouping of the stored biotas, with everything needed to prove it is
        /// still current. Guarded by <see cref="stateLock"/> like everything else it covers.
        ///
        /// A memo, never a persisted structure. The grouping design forbids storing a group anywhere -
        /// no group id on a biota, no table, no column, no schema change - and permits an in-memory
        /// cache keyed on the FULL version stamp, because that stamp already gates the vendor's own
        /// rebuild (PersonalVendor.cs:1526). Keying it on LESS than the version is forbidden; what is
        /// below keys it on the version AND MORE, which is strictly stronger.
        ///
        /// It earns its place: <see cref="EntryCountLocked"/> runs on every deposit's cap check, and
        /// grouping is a pairwise reflective diff, so recomputing it per deposit against a few hundred
        /// bags would put a quadratic walk on the deposit path.
        ///
        /// THE VERSION STAMP ALONE IS NOT A SUFFICIENT KEY, and an earlier revision of this comment was
        /// wrong to claim it was. It argued that a mutation landing mid-build could only make the memo
        /// look STALE, never fresher than its contents. That holds only if the version bump is
        /// collocated with the mutation under the same lock, and it is not: every mutation applies its
        /// change to <see cref="vaults"/> inside a `lock (stateLock)` block, RELEASES that lock, and
        /// only then runs `Interlocked.Increment(ref version)` - with a database round-trip in between
        /// on three of the paths (DepositToVault at the SaveBiota below the lock, WithdrawItem and
        /// WithdrawGroup likewise, TryReturnWithdrawn likewise). <see cref="GetEntries(int, int)"/> and
        /// <see cref="EntryCount"/> are plain locked readers and do NOT go through the mutation queue,
        /// so a reader can acquire stateLock inside that window and observe mutated vaults carrying an
        /// unchanged version. Keyed on the version alone the memo would then hand back the PRE-mutation
        /// group list, including WorldObject references to items already removed from their container.
        ///
        /// Two defences, and both are wanted:
        ///
        /// 1. <see cref="InvalidateGroupingLocked"/> is called by every locked section that mutates
        ///    vaults or their inventories, from INSIDE that same lock. This is the defence that closes
        ///    the window described above, and it closes it completely: every memo reader also runs
        ///    under stateLock, so the lock's happens-before guarantees any later reader sees the nulled
        ///    memo and rebuilds, whether or not Version has been bumped yet.
        /// 2. The memo also records the vault count and the total stored-item count it was built from,
        ///    and <see cref="GroupedStoredItemsLocked"/> rechecks both against live state on every call.
        ///    This is purely a SAFETY NET for a future mutation site that forgets defence 1: an
        ///    enumeration of call sites rots, a live structural check does not. It is not what closes
        ///    the window today.
        ///
        /// Keep both. Defence 1 is exact but depends on every future author remembering it; defence 2
        /// needs nobody to remember anything but cannot see a membership change that leaves both counts
        /// equal.
        /// </summary>
        private List<List<WorldObject>> groupingMemo;

        private long groupingMemoVersion = -1;

        private int groupingMemoVaultCount = -1;

        private int groupingMemoItemCount = -1;

        /// <summary>
        /// Fix round 2, F1: set when a ledger mutation COMMITTED but its resulting count could not be
        /// read back (<see cref="AccountVaultStackAdjustResult.AppliedCountUnknown"/>, or a
        /// <see cref="AccountVaultStackAdjustResult.Failed"/> that must be treated as such). While it
        /// is set, <see cref="ledger"/> is known to be stale for at least one wcid and must not be
        /// served.
        ///
        /// Guarded by <see cref="stateLock"/>, like everything else it protects.
        /// </summary>
        private bool ledgerReloadPending;

        private readonly object queueLock = new object();
        private readonly Queue<Action> queue = new Queue<Action>();
        private readonly object drainLock = new object();
        private volatile int mutationThreadId;

        /// <summary>
        /// Set once by <see cref="TryEvict"/>, and only ever under <see cref="drainLock"/>. Volatile so
        /// <see cref="IsEvicted"/> can be read without taking that lock, which matters because
        /// AccountVaultManager.GetStore reads it on a player's command path and must not block behind
        /// somebody else's mutation.
        /// </summary>
        private volatile bool evicted;

        private int openWindows;

        /// <summary>
        /// Items whose biota save has not been confirmed, with an attempt count. Deposits fill it, and
        /// so does a barreling: an item that moved into the barrel has the same problem in the same
        /// shape, since nothing else will ever re-save it either.
        ///
        /// A deposit's save is the ONLY thing that records the item's new home: the sell path flushes
        /// ContainerId = null to the shard before ProcessItemsForPurchase runs, so until this save
        /// lands the on-disk row is an orphan, and nothing else ever re-saves a vault item - vault
        /// containers are filtered out of landblocks, so Landblock.SaveDB never sees them, and the item
        /// is in no player's possessions. A dropped write is therefore a lost item, not a lost update.
        ///
        /// Guarded by stateLock. Drained under drainLock - from <see cref="Drain"/> on the mutation
        /// queue, and from <see cref="TryEvict"/> before an idle store is retired - so the retry cannot
        /// race a deposit, and a store is never dropped with a save still unconfirmed.
        /// </summary>
        private readonly Dictionary<WorldObject, int> pendingDepositSaves = new Dictionary<WorldObject, int>();

        private const int MaxDepositSaveAttempts = 3;

        /// <summary>
        /// The SAME world source this store was constructed with, exposed so a caller that has to
        /// observe a save result can route through it instead of calling WorldObject.SaveBiotaToDatabase
        /// and discarding the answer.
        ///
        /// Deliberately NOT a static on AccountVaultManager: the manager builds stores as
        /// `new AccountVaultStore(id)` and never holds a world source of its own, so a manager-level
        /// accessor would have to construct a SECOND one - which would bypass the test fake and make
        /// the vendor's save path unreachable from a unit test.
        /// </summary>
        internal IAccountVaultWorldSource World => world;

        public uint AccountId { get; }

        /// <summary>Unix time of the last read, mutation or window change. Drives idle eviction.</summary>
        public double LastTouchedUnixTime { get; private set; }

        /// <summary>
        /// Bumped by every mutation that can change what <see cref="GetEntries(int, int)"/> returns, so
        /// a view can tell whether it is stale without diffing contents. PersonalVendor.RebuildView is
        /// the only consumer today: it is called on EVERY approach, and the rejected-buy path
        /// re-approaches with no cooldown, so rebuilding unconditionally burned a dynamic guid per row
        /// per approach.
        ///
        /// Read without a lock, and the safety of that does NOT rest on all writers sharing one thread,
        /// because they do not: <see cref="Enqueue"/> runs queued work on the CALLING thread under
        /// drainLock, so writers are mutually excluded but each runs on whichever thread enqueued it,
        /// and <see cref="TryReapEmptyVault"/> does not go through the queue at all. Two things make a
        /// lock-free read safe instead. The write is an <see cref="Interlocked.Increment(ref long)"/>,
        /// so it is atomic against a concurrent one. And the reader stamps this value BEFORE it reads
        /// the contents, so the version it records can never be newer than the state it built from -
        /// the worst a race can do is record a stamp one behind, which costs one extra rebuild later
        /// and never a stale panel.
        /// </summary>
        public long Version => Volatile.Read(ref version);

        private long version;

        internal AccountVaultStore(uint accountId, IAccountVaultBackend backend = null, IAccountVaultWorldSource world = null)
        {
            AccountId = accountId;

            this.backend = backend ?? new ShardAccountVaultBackend();
            this.world = world ?? new ShardAccountVaultWorldSource();

            LastTouchedUnixTime = Time.GetUnixTime();

            // Outside stateLock by construction - nothing holds it yet - which is the whole reason the
            // mule-space bonus is computed here rather than in TryEnsureLoadedLocked. See
            // RefreshMuleSpaceBonus for the lock-ordering rule this obeys.
            RefreshMuleSpaceBonus();
        }

        #region Capacity cap

        /// <summary>
        /// account_vault_entry_cap, clamped to a sane int.
        ///
        /// Counts ENTRIES, where one entry is one stored biota OR one collapsed ledger row - never the
        /// number of units held (DESIGN 7.3). 10,000 plain healing kits are ONE entry; ten
        /// individually tinkered arrows are ten. Enforced at deposit only and NEVER retroactively:
        /// lowering it below an account's existing holdings refuses further deposits and leaves
        /// everything withdrawable.
        ///
        /// This is the BASE cap, the same for every account. Nothing should enforce or display it
        /// directly - read <see cref="EffectiveEntryCap"/>, which adds the account's own
        /// AugmentationMuleSpace bonus. It was renamed from EntryCap for exactly that reason
        /// (2026-09-05): the rename turned every existing reference into a compile error that had to be
        /// re-pointed deliberately, so no enforcement site could be silently left reading the base.
        /// </summary>
        public static int BaseEntryCap
        {
            get
            {
                var value = PropertyManager.GetLong("account_vault_entry_cap").Item;

                if (value < 1)
                    return 1;

                if (value > int.MaxValue)
                    return int.MaxValue;

                return (int)value;
            }
        }

        /// <summary>
        /// Extra entries this account has bought, cached. Always <see cref="CustomAugBroker.MuleSpaceEntriesPerAug"/>
        /// times the account's AugmentationMuleSpace count, clamped to [0, int.MaxValue].
        ///
        /// CACHED, and that is a locking requirement rather than an optimization. The enforcement point
        /// (<see cref="HasRoomForLocked"/>) runs under <see cref="stateLock"/>, while the count comes from
        /// PlayerManager.GetAccountPlayersSnapshot, which takes PlayerManager's own read lock
        /// (PlayerManager.cs:410). Nesting the two would introduce a lock order this class has nowhere
        /// else, so the count is computed OUTSIDE stateLock - once in the constructor and again from
        /// <see cref="RefreshMuleSpaceBonus"/> - and every reader inside the lock sees only this field.
        /// Same "compute once, cache for the session" shape as Player_AltCharacterBonus's
        /// altCharacterBonusThreshold.
        ///
        /// Written and read as a plain int: a torn read is not possible for an aligned 32-bit field, and
        /// the worst a race between a refresh and a deposit can do is enforce the previous bonus for one
        /// deposit, which is a delayed benefit and never a lost one.
        /// </summary>
        private int muleSpaceBonusEntries;

        /// <summary>
        /// The cap this account actually gets: the base cap plus 100 entries per AugmentationMuleSpace
        /// held anywhere on the account (the augmentation is bought and counted account-wide, which is the
        /// whole point of it). EVERY enforcement site and every player-facing "N of M entries" line reads
        /// this, never <see cref="BaseEntryCap"/>.
        ///
        /// Clamped so it can neither overflow nor fall below the base: a bonus is a bonus, so a corrupt or
        /// absurd property value can only ever fail to help.
        ///
        /// Safe to read from inside <see cref="stateLock"/> - it touches only <see cref="BaseEntryCap"/>
        /// (a PropertyManager read) and the cached <see cref="muleSpaceBonusEntries"/>, never PlayerManager.
        /// </summary>
        public int EffectiveEntryCap
        {
            get
            {
                var baseCap = BaseEntryCap;

                var bonus = muleSpaceBonusEntries;

                if (bonus <= 0)
                    return baseCap;

                var effective = (long)baseCap + bonus;

                if (effective > int.MaxValue)
                    return int.MaxValue;

                return (int)effective;
            }
        }

        /// <summary>
        /// Recomputes <see cref="muleSpaceBonusEntries"/> from the account's characters. Called from the
        /// constructor, and again by the Custom Dreamweave Augmentation broker's grant path (through
        /// AccountVaultManager.RefreshMuleSpaceBonus) after a MuleSpace trade, so a player who buys vault
        /// space sees it immediately rather than on the next store load.
        ///
        /// MUST be called with <see cref="stateLock"/> NOT held - it reaches PlayerManager, which takes its
        /// own read lock. This method deliberately takes no lock of its own for that reason.
        ///
        /// A missed call is a delayed benefit, never a lost one: the next store this account loads
        /// recomputes it from scratch.
        /// </summary>
        public void RefreshMuleSpaceBonus()
        {
            SetMuleSpaceBonusEntries(CustomAugBroker.AccountAugCount(AccountId, PropertyInt.AugmentationMuleSpace));
        }

        /// <summary>
        /// Sets the cached bonus from an already-counted number of augmentations. Split out from
        /// <see cref="RefreshMuleSpaceBonus"/> so a unit test can exercise <see cref="EffectiveEntryCap"/>'s
        /// arithmetic and clamp without a populated PlayerManager, which in the test assembly always
        /// reports an empty account. Visible to ACE.Server.Tests via InternalsVisibleTo
        /// (ACE.Server.csproj:15).
        /// </summary>
        internal void SetMuleSpaceBonusEntries(int augCount)
        {
            if (augCount <= 0)
            {
                muleSpaceBonusEntries = 0;
                return;
            }

            var bonus = (long)augCount * CustomAugBroker.MuleSpaceEntriesPerAug;

            muleSpaceBonusEntries = bonus > int.MaxValue ? int.MaxValue : (int)bonus;
        }

        /// <summary>
        /// Stored biotas plus non-zero ledger rows.
        ///
        /// Returns 0 when the store is not READY, which is why every internal caller checks
        /// <see cref="IsLoaded"/> first: a zero here is "unknown", not "empty".
        ///
        /// Ready, not merely index-loaded. Answering from the index alone would return a SHORT number
        /// while one of three vaults was still finishing its async inventory load (R4) - a real count,
        /// just not of everything the account owns - and a short count is indistinguishable from a
        /// true one at every call site. <see cref="GetEntries(int, int)"/> already refuses in that
        /// state; this uses the same gate so the two cannot disagree.
        /// </summary>
        public int EntryCount
        {
            get
            {
                lock (stateLock)
                {
                    if (!CheckReadyLocked(out _))
                        return 0;

                    return EntryCountLocked();
                }
            }
        }

        /// <summary>
        /// GROUP ROWS plus non-zero ledger rows, not stored biotas.
        ///
        /// Changed deliberately by the 2026-08-31 grouping design and signed off by the repo owner: the
        /// cap now bounds RENDERED PANEL ROWS and no longer bounds stored biotas. That matches the
        /// cap's own documented purpose, which states the number "equals the number of lines the client
        /// vendor panel is asked to render" (PropertyManager.cs, account_vault_entry_cap). Nothing is
        /// stranded if it is ever reversed, because the cap is enforced at deposit only and never
        /// retroactively (DESIGN 7.3).
        /// </summary>
        private int EntryCountLocked()
        {
            return GroupedStoredItemsLocked().Count + ledger.Count(kvp => kvp.Value > 0);
        }

        /// <summary>
        /// Whether depositing <paramref name="item"/> right now would consume a new entry against
        /// <see cref="EffectiveEntryCap"/> (fix round 2, F1). DESIGN 7.3: the cap counts ENTRIES, not units, so
        /// a pristine item that would only top up a ledger row the account already holds adds nothing
        /// and must not be counted against the cap - see TryDeposit's own cap check for the case this
        /// exists to keep free (a 9,001st plain healing kit on an already-held row). A caller checking
        /// capacity before TryDeposit (CanAcceptCore's pre-flight is the reason this exists) must query
        /// it here rather than re-deriving the same condition, which has already drifted out of sync
        /// with TryDeposit once.
        ///
        /// The rule itself lives in <see cref="AddsEntryLocked"/>, which this and TryDeposit's own cap
        /// check both call. That is the single source: this method is the outside-the-lock wrapper, not
        /// the rule, and an earlier version of this remark claimed to be the rule while TryDeposit went
        /// on re-deriving the identical expression inline - exactly the duplication it warned against.
        ///
        /// IsPristine is called OUTSIDE the lock, matching the discipline TryDeposit's own remark
        /// documents: it takes the item's own BiotaDatabaseLock read lock, and this store must never
        /// hold stateLock while doing that.
        /// </summary>
        internal bool WouldAddEntry(WorldObject item)
        {
            var pristine = world.IsPristine(item);

            lock (stateLock)
            {
                // Gated on READY, exactly as EntryCount is, and for the same reason: an unloaded store
                // reports an empty ledger and empty vaults, so a deposit that really would join a row
                // the account already draws is counted as adding a new one. The conservative answer for
                // a store that cannot be read is "yes, it adds an entry" - it can only refuse a deposit
                // that would have been free, never admit one past the cap.
                if (!CheckReadyLocked(out _))
                    return true;

                return AddsEntryLocked(pristine, item);
            }
        }

        /// <summary>
        /// DESIGN 7.3's entry rule, and the only place it is written down: a deposit consumes a new
        /// entry unless it lands on a row the account is ALREADY rendering. Callers must have computed
        /// <paramref name="pristine"/> outside every lock (see <see cref="WouldAddEntry"/>) and must
        /// hold <see cref="stateLock"/> when calling this.
        ///
        /// There are now two ways to land on an existing row, and they are the same shape:
        /// - a pristine item topping up a ledger row the account already holds; and
        /// - a non-pristine item joining an existing GROUP row (the 2026-08-31 grouping design).
        ///
        /// The second was added with grouping and is not optional: without it the cap would count a row
        /// that <see cref="EntryCountLocked"/> does not, and a vault of equivalent salvage would refuse
        /// deposits while its panel showed a handful of rows.
        /// </summary>
        private bool AddsEntryLocked(bool pristine, WorldObject item)
        {
            if (pristine)
                return !ledger.TryGetValue(item.WeenieClassId, out var held) || held <= 0;

            return !JoinsAnExistingGroupLocked(item);
        }

        /// <summary>
        /// True if <paramref name="newEntries"/> more entries fit under <see cref="EffectiveEntryCap"/> given what
        /// this store holds right now. This is TryDeposit's own cap arithmetic (<c>count &gt;= cap</c>
        /// refuses a deposit that adds one) generalized to N entries at once and extracted to a single
        /// place, so a caller that needs to know capacity for several prospective entries BEFORE
        /// committing anything - the market purchase preflight is the reason this exists - cannot
        /// re-derive the rule and drift from what TryDeposit will actually enforce. Caller must hold
        /// <see cref="stateLock"/>.
        /// </summary>
        private bool HasRoomForLocked(int newEntries)
        {
            if (newEntries <= 0)
                return true;

            // EffectiveEntryCap, never BaseEntryCap: this is THE enforcement point, and reading the base
            // here is how a bought bonus ends up displayed everywhere and applied nowhere. It reads the
            // CACHED muleSpaceBonusEntries and never PlayerManager, so calling it under stateLock is safe.
            return EntryCountLocked() + newEntries <= EffectiveEntryCap;
        }

        /// <summary>
        /// Outside-the-lock wrapper for <see cref="HasRoomForLocked"/>, matching <see cref="EntryCount"/>'s
        /// and <see cref="WouldAddEntry"/>'s discipline: gated on READY, because an unloaded store cannot
        /// prove it has room and the conservative answer is "no". <paramref name="newEntries"/> is the
        /// number of NEW rows a prospective delivery would need, not the number of units.
        /// </summary>
        public bool HasRoomFor(int newEntries)
        {
            lock (stateLock)
            {
                if (!CheckReadyLocked(out _))
                    return false;

                return HasRoomForLocked(newEntries);
            }
        }

        #endregion

        #region Read-time grouping (Docs/MuleVendor/2026-08-31-vault-grouping-design.md)

        /// <summary>
        /// The cheap half of the grouping rule: the bucket an item falls into before any diff is run.
        ///
        /// (WeenieClassId, Structure ?? -1, workmanship rounded to two decimals ?? absent). Rounding to
        /// what the CLIENT renders is deliberate rather than sloppy: two bags both reading "6.42" must
        /// group even though their raw (ItemWorkmanship, NumItemsInMaterial) pairs differ - (77, 12) and
        /// (154, 24) are the worked example.
        ///
        /// Structure is in the key rather than in the tolerated-difference set because equal Structure
        /// is also what makes equal Name free: a bag is named $"Salvage ({Structure})"
        /// (Player_Crafting.cs:286).
        ///
        /// THE QUOTIENT IS COMPUTED HERE AND NOT READ FROM WorldObject.Workmanship, and that is a
        /// correctness requirement rather than a style choice. That getter is not a getter: when the
        /// quotient falls outside [1, 10] it REWRITES ItemWorkmanship in place through SetProperty
        /// (WorldObject_Properties.cs:1587-1598). Calling it from here would make a nominally read-only
        /// grouping computation mutate a persisted property of a stored biota on every deposit,
        /// withdraw, EntryCount and GetEntries, under stateLock only, with no BiotaDatabaseLock and no
        /// audit row. Reads of this store do not write to items.
        ///
        /// The arithmetic below is deliberately identical to the getter's FIRST line - the same float
        /// division widened to double and rounded at two decimals - AND THE RECOVERY AND CLAMP BRANCH
        /// TOO, minus its write. The key is therefore what that getter would have RETURNED on its first
        /// read, for every input rather than only for in-range ones, and it is now stable on every
        /// later read as well, which it could not be while the first read rewrote the value the second
        /// read keyed on.
        ///
        /// Replicating the recovery branch is not optional tidiness. An earlier revision dropped it and
        /// claimed that was conservative because it could only SPLIT rows. That was false, because the
        /// bucket key - not the diff - is the real gate on whether a workmanship difference matters:
        /// VaultCollapse.AreGroupable forgives ItemWorkmanship and NumItemsInMaterial outright once two
        /// items share a bucket. Legacy botched-formula data makes that reachable. Two bags at
        /// Structure 5, one (ItemWorkmanship 50000, NumItemsInMaterial 1) and one (100000, 2), have the
        /// same RAW quotient of 50000 and would have landed in one bucket and been merged - while the
        /// recovery formula, which reads ItemWorkmanship and Structure and ignores NumItemsInMaterial,
        /// separates them as 1.0 and 2.0. They also appraise at different workmanship tiers once
        /// withdrawn, because the client-visible getter still clamps. So the row would have merged two
        /// items the player can tell apart.
        ///
        /// The cast is guarded rather than trusted. A double-to-int conversion in .NET SATURATES, it
        /// does not throw and is not unspecified: (int)3e9 is int.MaxValue, (int)double.NegativeInfinity
        /// is int.MinValue, (int)double.NaN is 0. int.MinValue is exactly the "no workmanship at all"
        /// sentinel, so an unguarded cast lets a corrupted item bucket with every item that has none.
        /// ItemWorkmanship has no range validation where it is written - AdminCommands.cs:4676-4688
        /// parses an admin-typed float straight into it - so a large negative value needs no zero
        /// denominator to get there. The clamp above happens to bound every path that reaches the cast
        /// today; the guard stays because it protects the CAST, not the arithmetic that fed it, and it
        /// must keep working if the clamp is ever changed.
        /// </summary>
        private static (uint Wcid, int Structure, int Workmanship) BucketKey(WorldObject item)
        {
            const int noWorkmanship = int.MinValue;
            const int unkeyableWorkmanship = int.MinValue + 1;

            var structureKey = item.Structure.HasValue ? item.Structure.Value : -1;

            // GetProperty, never the ItemWorkmanship/Workmanship properties: plain dictionary reads
            // with no setter behind them. WorldObject.Workmanship in particular is not a getter - it
            // rewrites ItemWorkmanship in place (WorldObject_Properties.cs:1587-1598) - and a read of
            // the vault does not write to a stored item.
            var itemWorkmanship = item.GetProperty(PropertyInt.ItemWorkmanship);

            if (itemWorkmanship == null)
                return (item.WeenieClassId, structureKey, noWorkmanship);

            var numItemsInMaterial = item.GetProperty(PropertyInt.NumItemsInMaterial) ?? 1;

            var workmanship = (float)itemWorkmanship.Value / numItemsInMaterial;

            if (workmanship < 1.0f || workmanship > 10.0f)
            {
                // Structure defaults to 1 HERE, not to the -1 the bucket key uses for "absent". Two
                // different questions, and conflating them would change the recovered value.
                var structure = item.Structure ?? 1;

                workmanship = (float)itemWorkmanship.Value / 10000 / structure;
                workmanship = Math.Clamp(workmanship, 1.0f, 10.0f);
            }

            var scaled = Math.Round(workmanship * 100.0);

            // NaN reaches here from 0/0 (a zero ItemWorkmanship with a zero Structure), and Math.Clamp
            // passes NaN through rather than clamping it.
            if (double.IsNaN(scaled) || scaled <= int.MinValue + 2 || scaled >= int.MaxValue)
                return (item.WeenieClassId, structureKey, unkeyableWorkmanship);

            return (item.WeenieClassId, structureKey, (int)scaled);
        }

        /// <summary>
        /// Whether an item is eligible to be grouped at all.
        ///
        /// ItemType.TinkeringMaterial exactly, which covers salvage bags, the five mod hammers and the
        /// Hollow Hammer and excludes everything else. The restriction is NOT incidental: the tolerated
        /// difference set includes Value, which is cosmetic on a salvage bag and meaningful on a
        /// weapon, so applying the rule generally would merge two differently-priced weapons into one
        /// row and give the player no way to tell which one they were about to withdraw.
        ///
        /// Exact equality rather than a flag test, and that is the conservative direction: ItemType is
        /// a [Flags] enum, so an item carrying TinkeringMaterial alongside some other bit declines to
        /// group rather than grouping on a partial match.
        /// </summary>
        private static bool IsGroupCandidate(WorldObject item)
        {
            return item != null && item.ItemType == ItemType.TinkeringMaterial;
        }

        /// <summary>
        /// Every stored biota, in the order <see cref="GetEntries(int, int)"/> documents: vault order,
        /// then placement order, then guid.
        /// </summary>
        private IEnumerable<WorldObject> EnumerateStoredItemsLocked()
        {
            foreach (var vault in vaults)
            {
                foreach (var item in vault.Inventory.Values.OrderBy(i => i.PlacementPosition ?? 0).ThenBy(i => i.Guid.Full))
                    yield return item;
            }
        }

        /// <summary>
        /// The stored biotas bucketed into groups, memoized on <see cref="Version"/>.
        ///
        /// Grouping spans ALL of the account's vaults rather than being done per container, and that
        /// matters as soon as an account holds more than one: vaults exist purely because a container
        /// holds 255 items, they are a storage detail the player never sees, and per-container grouping
        /// would draw two rows for one logical group the moment a 256th bag arrived. It would also put
        /// <see cref="AddsEntryLocked"/> permanently out of step with <see cref="EntryCountLocked"/>,
        /// since a deposit lands in whichever vault has a free slot rather than the one holding its
        /// group.
        ///
        /// Each group is emitted at its REPRESENTATIVE's position in that order, so the ordering
        /// GetEntries documents - deterministic within a session, not stable across a restart - is
        /// preserved exactly.
        /// </summary>
        private List<List<WorldObject>> GroupedStoredItemsLocked()
        {
            // All three read BEFORE the build, so a stamp can only ever be one step behind the contents
            // it labels, never ahead of them. See groupingMemo's remarks for why the version alone is
            // not enough and what the two count checks are actually defending.
            var stamp = Volatile.Read(ref version);
            var vaultCount = vaults.Count;
            var itemCount = StoredItemCountLocked();

            if (groupingMemo != null
                && groupingMemoVersion == stamp
                && groupingMemoVaultCount == vaultCount
                && groupingMemoItemCount == itemCount)
                return groupingMemo;

            var groups = new List<List<WorldObject>>();

            // Bucket key -> the groups already open in that bucket. Several can be open at once: two
            // bags can share a bucket and still differ somewhere the diff refuses to tolerate.
            var buckets = new Dictionary<(uint, int, int), List<List<WorldObject>>>();

            foreach (var item in EnumerateStoredItemsLocked())
            {
                if (!IsGroupCandidate(item))
                {
                    groups.Add(new List<WorldObject> { item });
                    continue;
                }

                var key = BucketKey(item);

                if (!buckets.TryGetValue(key, out var open))
                {
                    open = new List<List<WorldObject>>();
                    buckets[key] = open;
                }

                var joined = false;

                foreach (var group in open)
                {
                    // VaultCollapse.Diff takes NO lock (unlike IsPristine, which takes the item's own
                    // BiotaDatabaseLock and must therefore be called from outside every lock), so this
                    // is safe under stateLock. It reaches no database either.
                    if (!VaultCollapse.AreGroupable(item.Biota, group[0].Biota))
                        continue;

                    group.Add(item);
                    joined = true;
                    break;
                }

                if (joined)
                    continue;

                var opened = new List<WorldObject> { item };

                open.Add(opened);
                groups.Add(opened);
            }

            groupingMemoVersion = stamp;
            groupingMemoVaultCount = vaultCount;
            groupingMemoItemCount = itemCount;
            groupingMemo = groups;

            return groups;
        }

        /// <summary>Stored biotas across every vault. Caller holds <see cref="stateLock"/>.</summary>
        private int StoredItemCountLocked()
        {
            var count = 0;

            foreach (var vault in vaults)
                count += vault.Inventory.Count;

            return count;
        }

        /// <summary>
        /// Drops the grouping memo. MUST be called from inside the same `lock (stateLock)` block that
        /// mutates <see cref="vaults"/> or any vault's inventory, never after it - the whole defect this
        /// exists for is a reader acquiring stateLock in the gap between a mutation and its bookkeeping.
        ///
        /// It is deliberately NOT called for ledger mutations: the memo covers stored biotas only, and
        /// <see cref="EntryCountLocked"/> counts the ledger fresh on every call.
        /// </summary>
        private void InvalidateGroupingLocked()
        {
            groupingMemo = null;
            groupingMemoVersion = -1;
            groupingMemoVaultCount = -1;
            groupingMemoItemCount = -1;
        }

        /// <summary>
        /// Whether depositing <paramref name="item"/> would land it in a group the account is already
        /// rendering, so the deposit costs no new entry. Caller holds <see cref="stateLock"/>.
        /// </summary>
        private bool JoinsAnExistingGroupLocked(WorldObject item)
        {
            if (!IsGroupCandidate(item))
                return false;

            var key = BucketKey(item);

            foreach (var group in GroupedStoredItemsLocked())
            {
                var representative = group[0];

                if (!IsGroupCandidate(representative) || !BucketKey(representative).Equals(key))
                    continue;

                if (VaultCollapse.AreGroupable(item.Biota, representative.Biota))
                    return true;
            }

            return false;
        }

        #endregion

        #region Loading (R4)

        /// <summary>
        /// True only when the index and ledger have been read AND every one of the account's vaults
        /// reports InventoryLoaded.
        ///
        /// Reading this triggers a load if one has not happened or the last one failed, so a transient
        /// shard outage heals on the next access rather than sticking the store dead.
        /// </summary>
        public bool IsLoaded
        {
            get
            {
                lock (stateLock)
                {
                    if (!TryEnsureLoadedLocked())
                        return false;

                    return AllVaultsLoadedLocked();
                }
            }
        }

        private bool AllVaultsLoadedLocked()
        {
            // AND across EVERY vault, not just the first (R4). Answering from vaults[0] alone would
            // report a short list and let a withdraw transact against a vault this store cannot see.
            foreach (var vault in vaults)
            {
                if (!vault.InventoryLoaded)
                    return false;
            }

            return true;
        }

        private bool CheckReadyLocked(out string failReason)
        {
            if (!TryEnsureLoadedLocked())
            {
                failReason = UnavailableMessage;
                return false;
            }

            if (!AllVaultsLoadedLocked())
            {
                failReason = StillLoadingMessage;
                return false;
            }

            failReason = null;
            return true;
        }

        /// <summary>
        /// Public wrapper for <see cref="CheckReadyLocked"/> (fix round 2, F4). Every internal caller
        /// of CheckReadyLocked already holds <see cref="stateLock"/> when it calls in; this is for a
        /// caller outside this class - CanAcceptCore's pre-flight is the reason this exists - that
        /// needs the same UnavailableMessage-versus-StillLoadingMessage distinction TryDeposit and
        /// TryWithdraw get, rather than the collapsed single false <see cref="IsLoaded"/> returns for
        /// both an index-load failure and vaults still finishing their async load.
        /// </summary>
        internal bool TryCheckReady(out string failReason)
        {
            lock (stateLock)
                return CheckReadyLocked(out failReason);
        }

        /// <summary>
        /// Reads the vault index and the ledger, and rehydrates every container.
        ///
        /// Returns false without changing any state if ANY part of that fails. A null from either read
        /// is a database failure, never an empty account, and a container row whose biota could not be
        /// READ fails the whole store rather than being skipped: skipping it would silently shrink the
        /// account's vault set, which is how insertion ends up creating a fresh container while the
        /// player's items sit in one this store forgot about.
        ///
        /// A container row whose biota is ABSENT is the one case that does not fail the store, and the
        /// difference is not a softening of the rule above - it is the rule applied to a different
        /// fact. A dangling index row otherwise bricks the account permanently: the store refuses,
        /// indexLoaded never flips, and every item in every OTHER vault the account owns is
        /// inaccessible with it, with no admin command and no self-heal. The window that produces one
        /// is real - the container biota save and the index-row insert are two separate writes - and
        /// dropping the row is safe because a container whose biota does not exist provably has no
        /// contents, while DESIGN 7.2's orphan purge already deletes children whose parent container
        /// is missing. See <see cref="VaultContainerLoad"/>.
        /// </summary>
        private bool TryEnsureLoadedLocked()
        {
            if (indexLoaded)
            {
                // Fix round 2, F1. A committed ledger delta whose count could not be read back leaves
                // this store holding a number that is provably wrong for one wcid. Re-read the ledger
                // before serving anything, and REFUSE if that read fails rather than serving the stale
                // count: the vault's whole null-versus-empty discipline rests on never presenting a
                // number this store cannot stand behind, and a wrong count is worse than a refusal
                // because the player acts on it. The reload is only the ledger - the vault index and
                // the containers are untouched by a ledger delta, and rebuilding them would throw away
                // every container's finished async inventory load (R4) for nothing.
                if (!ledgerReloadPending)
                    return true;

                return TryReloadLedgerLocked();
            }

            var rows = backend.GetAccountVaults(AccountId);

            if (rows == null)
            {
                log.Error($"[VAULT] account {AccountId}: could not read the vault index; refusing rather than treating it as an empty vault.");
                return false;
            }

            var stacks = backend.GetAccountVaultStacks(AccountId);

            if (stacks == null)
            {
                log.Error($"[VAULT] account {AccountId}: could not read the stack ledger; refusing rather than treating it as an empty ledger.");
                return false;
            }

            var loaded = new List<Container>();
            var rowIds = new Dictionary<uint, uint>();

            Container loadedBarrel = null;
            uint loadedBarrelRowId = 0;

            foreach (var row in rows)
            {
                var outcome = world.LoadContainer(row.ContainerGuid, out var container);

                // The barrel is one more container on the account, marked by account_vault.kind. It
                // is partitioned out HERE rather than filtered later, so that nothing downstream has
                // to remember it exists: every path that walks `vaults` is then correct because the
                // barrel was never in the list.
                var isBarrel = row.Kind == BarrelContainerKind;

                if (outcome == VaultContainerLoad.Absent)
                {
                    log.Error($"[VAULT] account {AccountId}: {(isBarrel ? "barrel" : "vault")} container 0x{row.ContainerGuid:X8} (account_vault row {row.Id}) has NO BIOTA. Deleting that one index row and loading the rest of the account's containers, because refusing here would make every other vault on the account permanently inaccessible. An empty container is the only thing this can have been.");

                    if (!backend.DeleteAccountVault(row.Id))
                        log.Error($"[VAULT] account {AccountId}: could not delete dangling account_vault row {row.Id}. It is harmless but will be retried on the next load.");

                    continue;
                }

                if (outcome != VaultContainerLoad.Loaded || container == null)
                {
                    if (isBarrel)
                    {
                        // THE ONE PLACE THIS LOOP DELIBERATELY DOES NOT FAIL CLOSED, and the asymmetry
                        // is the point. A vault container that cannot be READ refuses the whole store,
                        // because proceeding would hide the player's items rather than lose them
                        // loudly. The barrel holds nothing the player can see, ask for or act on - it
                        // holds only what they have already destroyed - so refusing the store over it
                        // would take a working vault away from someone over a container they cannot
                        // even look at. It degrades instead: the barrel is absent, TryBarrel refuses
                        // until a later load reads it, and the vault is untouched.
                        log.Error($"[VAULT] account {AccountId}: barrel container 0x{row.ContainerGuid:X8} (account_vault row {row.Id}) could not be READ. Continuing WITHOUT it, because the barrel is not player-visible and refusing the whole store would take the player's vault away over something they cannot see. Barreling and restoring are unavailable for this account until a later load succeeds.");
                        continue;
                    }

                    log.Error($"[VAULT] account {AccountId}: vault container 0x{row.ContainerGuid:X8} (account_vault row {row.Id}) could not be READ. The whole store is refused until it can be, because proceeding would hide items rather than lose them loudly.");
                    return false;
                }

                if (isBarrel)
                {
                    if (loadedBarrel != null)
                    {
                        // An account has at most one barrel. A second kind 1 row is a data fault, not a
                        // shape to support: keep the first (oldest, since the read is ordered by
                        // created_At) and say so, rather than silently picking one.
                        log.Error($"[VAULT] account {AccountId}: a SECOND barrel container 0x{row.ContainerGuid:X8} (account_vault row {row.Id}) exists alongside 0x{loadedBarrel.Guid.Full:X8}. Using the older one and ignoring this row; nothing is destroyed, because a barrel may still hold items an admin can restore.");
                    }
                    else
                    {
                        loadedBarrel = container;
                        loadedBarrelRowId = row.Id;
                    }

                    // R1, second half, exactly as for a vault container: the barrel biota must never
                    // enter a landblock either.
                    AccountVaultSpawnFilter.Register(row.ContainerGuid);
                    continue;
                }

                loaded.Add(container);
                rowIds[row.ContainerGuid] = row.Id;

                // R1, second half: teach the spawn filter this guid, so it is kept out of the world in
                // ANY landblock and not only in the reserved one.
                AccountVaultSpawnFilter.Register(row.ContainerGuid);
            }

            vaults.Clear();
            vaults.AddRange(loaded);

            // Assigned in the same locked block as the vault list, from locals, so a reader can never
            // see a half-swapped pair.
            barrel = loadedBarrel;
            barrelRowId = loadedBarrelRowId;

            // Every stored item this store can see has just been replaced. The caller holds stateLock.
            InvalidateGroupingLocked();

            vaultRowIds.Clear();

            foreach (var kvp in rowIds)
                vaultRowIds[kvp.Key] = kvp.Value;

            ledger.Clear();

            foreach (var stack in stacks)
            {
                if (stack.Count > 0)
                    ledger[stack.Wcid] = stack.Count;
            }

            indexLoaded = true;
            ledgerReloadPending = false;
            return true;
        }

        /// <summary>
        /// Fix round 2, F1: marks <see cref="ledger"/> stale and tries to refresh it immediately.
        ///
        /// Called ONLY from the mutation queue, by the three ledger mutations, on the outcomes that
        /// mean "the units moved but the resulting count is unknown". Doing the reload here rather than
        /// leaving it entirely to the next reader is what makes the ordinary case invisible to the
        /// player: the mutation is already on the queue, so no other mutation can interleave with the
        /// re-read, and the correct count is in place before the vendor's next view rebuild.
        ///
        /// The flag is what makes it SAFE rather than merely prompt. If the re-read also fails, the
        /// flag stays set and <see cref="TryEnsureLoadedLocked"/> refuses the store until a later read
        /// succeeds - so the failure degrades to "your vault is unavailable", never to a wrong number.
        /// </summary>
        private void InvalidateLedger(string why)
        {
            lock (stateLock)
            {
                ledgerReloadPending = true;

                if (!indexLoaded)
                    return;     // nothing loaded to be stale; the first load will read the ledger anyway

                if (!TryReloadLedgerLocked())
                    log.Error($"[VAULT] account {AccountId}: could not re-read the stack ledger after {why}. The store will refuse reads and mutations until a later read succeeds, rather than serve a count it knows is stale.");
            }
        }

        /// <summary>
        /// Re-reads account_vault_stack into <see cref="ledger"/>. Caller must hold
        /// <see cref="stateLock"/>. Returns false, leaving the previous contents and
        /// <see cref="ledgerReloadPending"/> alone, if the read failed - a null is a read FAILURE and
        /// never an empty ledger, exactly as on the initial load.
        /// </summary>
        private bool TryReloadLedgerLocked()
        {
            var stacks = backend.GetAccountVaultStacks(AccountId);

            if (stacks == null)
                return false;

            ledger.Clear();

            foreach (var stack in stacks)
            {
                if (stack.Count > 0)
                    ledger[stack.Wcid] = stack.Count;
            }

            ledgerReloadPending = false;

            // The listed counts may have changed, so any open window is stale.
            Interlocked.Increment(ref version);

            return true;
        }

        #endregion

        #region Mutation queue (R3)

        /// <summary>
        /// True while the calling thread is executing work off this store's mutation queue.
        /// </summary>
        public bool IsOnMutationQueue => mutationThreadId == Environment.CurrentManagedThreadId;

        /// <summary>
        /// Runs on the mutation queue before <see cref="TryWithdraw"/> moves anything, with (accountId,
        /// itemGuid, wcid, amount); itemGuid is null for a ledger row. Static, because nothing here may
        /// name a type outside this namespace. A subscriber MUST NOT throw or call back into this store.
        /// </summary>
        public static Action<uint, uint?, uint, int> PreWithdrawHook;

        /// <summary>
        /// Answers whether (accountId, itemGuid, wcid) currently carries an ACTIVE market listing;
        /// itemGuid is null for a ledger row. Set by MarketManager on startup and cleared on shutdown,
        /// exactly as <see cref="PreWithdrawHook"/> is.
        ///
        /// A hook rather than a reference to MarketManager for the reason stated on the class doc:
        /// nothing in this namespace may name a market type, and taking that dependency would make
        /// this store untestable without the market's index.
        ///
        /// DEFAULTS TO "NOTHING IS LISTED", and that default is safe only because it is the SECOND of
        /// two checks rather than the only one. The market refuses a barrel of a listed item at its
        /// own layer (MarketManager.Barrel, which owns the listing index and answers item_listed); the
        /// check here is defence in depth for a caller that reaches the store directly, such as the
        /// /vaultrestore-adjacent admin paths or a future in-game command. A null hook on a server
        /// where the market never started means there are no listings to protect.
        ///
        /// A subscriber MUST NOT throw and MUST NOT call back into this store; a throw is caught and
        /// read as "not listed", because refusing every barreling on a market index hiccup would be
        /// the worse failure.
        /// </summary>
        public static Func<uint, uint?, uint, bool> IsListedHook;

        /// <summary>
        /// Work items WAITING to run - it does not count the one executing, so a zero here does not
        /// mean the store is quiet. For admin readouts and tests only. The idle sweep deliberately does
        /// not use it: see <see cref="TryEvict"/>.
        /// </summary>
        public int QueuedWork
        {
            get { lock (queueLock) return queue.Count; }
        }

        /// <summary>
        /// Runs <paramref name="work"/> on this store's single-threaded mutation queue, and does not
        /// return until it has run.
        ///
        /// This is the in-memory half of R3. Two vendor windows on one store are the case the sharing
        /// feature introduces; the ledger's guarded UPDATE adjudicates the ledger at the database, but
        /// the biota path - find a free slot, add, save - has no equivalent atomic statement and is
        /// protected only by this queue.
        ///
        /// Callers get the result out through a captured local:
        /// <code>
        /// var ok = false; string reason = null;
        /// store.Enqueue(() => ok = store.TryDeposit(item, player, out reason));
        /// </code>
        ///
        /// Returns false, having run nothing, when this store has been retired by the manager's idle
        /// sweep. Check it if the caller cached a store reference across any await, tick or player
        /// input; a caller that obtained the reference and used it in one go cannot observe a refusal,
        /// because Enqueue's own Touch beats the sweep's idle test.
        /// </summary>
        public bool Enqueue(Action work)
        {
            return Enqueue(work, out _);
        }

        /// <summary>
        /// <see cref="Enqueue(Action)"/>, plus the exception the work threw, if it threw (fix round 2,
        /// F4).
        ///
        /// The bool alone was ambiguous in the one direction that matters. <see cref="Drain"/> catches
        /// a work item's exception and does not rethrow - deliberately, because a throw must not strand
        /// the queue or the drain lock - so a closure of the standard shape
        /// (<c>() =&gt; ok = store.TryDeposit(...)</c>) leaves `ok` at its initialized false and
        /// Enqueue still reports true. The caller then reads a default false as "the store refused",
        /// which is a different fact with a different correct response: a refusal means nothing
        /// happened, while a throw means the mutation got some unknown distance through and may have
        /// credited the ledger, inserted into a vault, or written an audit row before it stopped.
        ///
        /// Reading the second as the first is what produced a ledger credit PLUS a hand-back to the
        /// player, and the same object in both vault.Inventory and a pack. The realistic trigger is not
        /// exotic: <c>BlockingCollection.Add</c> after <c>CompleteAdding()</c> throws, which is exactly
        /// what a deposit racing server shutdown does.
        ///
        /// <paramref name="thrown"/> is null when the work completed, whether it succeeded or refused.
        /// It is also null when this returns false, because then nothing ran at all.
        ///
        /// Drain's own catch is deliberately left in place: it protects the queue, and this only
        /// RECORDS what it is about to log.
        /// </summary>
        public bool Enqueue(Action work, out Exception thrown)
        {
            thrown = null;

            if (work == null)
                return false;

            Touch();

            // Re-entrancy. drainLock is a monitor, so a work item that calls Enqueue would otherwise
            // re-enter Drain and run every LATER item to completion INSIDE itself - an earlier
            // mutation finishing after a later one, which is precisely what the queue exists to
            // prevent. Running inline instead keeps "one mutation at a time, in order" true, and is
            // what RunOnQueue already does for the grant path.
            //
            // Nothing is recorded on this arm and nothing needs to be: an inline throw propagates
            // straight to the caller, which is the behaviour this path has always had, and a caller
            // that never returns from Enqueue has no out parameter to read.
            if (IsOnMutationQueue)
            {
                work();
                return true;
            }

            Exception captured = null;

            Action recorded = () =>
            {
                try
                {
                    work();
                }
                catch (Exception ex)
                {
                    captured = ex;
                    throw;      // Drain still logs it, and still keeps the queue and its lock intact
                }
            };

            lock (drainLock)
            {
                // Under drainLock, and therefore atomic against TryEvict's whole decision. A store
                // that has been retired must never accept work: the manager has already replaced it
                // in the table, so mutating it would put two mutation queues over one account, which
                // is R3 reopened by the back door (D1).
                if (evicted)
                {
                    log.Warn($"[VAULT] account {AccountId}: refused work on a store that has already been retired by the idle sweep. The caller is holding a stale store reference and should re-fetch it from AccountVaultManager.GetStore.");
                    return false;
                }

                lock (queueLock)
                    queue.Enqueue(recorded);
            }

            Drain();

            // `captured` is written on whichever thread drained this item and read here. Both sides
            // pass through drainLock - the drainer holds it while running the work, and this thread
            // acquires it inside Drain before returning - so the write is visible without any further
            // synchronization.
            thrown = captured;

            return true;
        }

        /// <summary>True once the idle sweep has retired this store. A retired store accepts no work.</summary>
        public bool IsEvicted => evicted;

        /// <summary>
        /// Retires this store if it is genuinely idle, and reports whether it may now be dropped from
        /// the manager's table.
        ///
        /// The ENTIRE decision lives here, under drainLock, rather than in the manager, and that is the
        /// point of the method. Checked from outside, the guards race: a caller that reads
        /// <see cref="QueuedWork"/> as zero and enqueues a moment later mutates a store the sweep has
        /// meanwhile dropped, while GetStore hands the next caller a second store over the same
        /// account. Deciding and marking under the same lock <see cref="Enqueue"/> takes closes that,
        /// because the loser of the race sees the other side's result rather than a stale copy of it.
        ///
        /// TryEnter, not lock: this runs on the world heartbeat, and a store that is mid-mutation is by
        /// definition not idle, so declining to wait costs one more sweep and nothing else.
        /// </summary>
        internal bool TryEvict(double currentUnixTime)
        {
            if (evicted)
                return true;

            if (!Monitor.TryEnter(drainLock))
                return false;

            try
            {
                if (evicted)
                    return true;

                if (OpenWindows > 0)
                    return false;

                lock (queueLock)
                {
                    if (queue.Count > 0)
                        return false;
                }

                if (currentUnixTime - LastTouchedUnixTime < IdleEvictionSeconds)
                    return false;

                // F4's retry is drained from Drain, i.e. from the NEXT enqueued mutation - and a deposit
                // whose save failed as the last act of a session has no next mutation. Retiring the
                // store here would drop the retry DepositToVault's ERROR line explicitly promises, in
                // exactly the case F4 describes: the item is in the vault in memory, the on-disk row is
                // still the orphan the sell path flushed, and nothing else ever re-saves a vault item.
                //
                // So the list is DRAINED here rather than merely declined against. drainLock is already
                // held, which is the same exclusion Drain runs queued work under, so this cannot race a
                // deposit or a withdrawal; drainLock -> stateLock is the direction the retry already
                // takes. It runs after the idle test rather than before it, so an active store never
                // spends an attempt on a sweep it was never going to be evicted on.
                //
                // This cannot pin a store forever. RetryPendingDepositSaves counts attempts and, once
                // MaxDepositSaveAttempts is exceeded, drops the entry and logs the give-up line - so the
                // list empties after a bounded number of sweeps whether the saves ever land or not, and
                // the sweep after that evicts normally.
                //
                // GUARDED (review round, finding 1). This runs on the world heartbeat, from
                // AccountVaultManager.Tick, which wraps nothing: an exception escaping here escapes the
                // whole idle sweep and takes every OTHER account's store with it for that pass.
                RetryPendingDepositSavesGuarded();

                lock (stateLock)
                {
                    if (pendingDepositSaves.Count > 0)
                    {
                        log.Error($"[VAULT] account {AccountId}: declining eviction with {pendingDepositSaves.Count} unconfirmed deposit save(s) outstanding.");
                        return false;
                    }
                }

                evicted = true;
                return true;
            }
            finally
            {
                Monitor.Exit(drainLock);
            }
        }

        private void Drain()
        {
            // Exactly one thread executes work at a time. A second caller blocks here, and when it
            // gets in it drains whatever is left - including its own item - so Enqueue never returns
            // before the work it queued has run.
            lock (drainLock)
            {
                while (true)
                {
                    Action next;

                    lock (queueLock)
                    {
                        if (queue.Count == 0)
                            return;

                        next = queue.Dequeue();
                    }

                    var previous = mutationThreadId;
                    mutationThreadId = Environment.CurrentManagedThreadId;

                    try
                    {
                        // Ahead of the work item rather than at the top of the loop, so it runs once
                        // per DEQUEUED item and with mutationThreadId already set. At the top it would
                        // also run on the iteration that finds the queue empty, which would re-save a
                        // deposit inside the very Enqueue whose callback had just reported it failed -
                        // a retry before the queue had drained, not on the next mutation.
                        //
                        // GUARDED (review round, finding 1), and it is not defensive padding. It shares
                        // this try with `next()`, so a throw from HERE aborts the iteration BEFORE the
                        // just-dequeued item ever runs. That item usually belongs to a different caller,
                        // which then gets thrown == null and its locals still at their defaults from
                        // Enqueue(work, out thrown) - and the contract that overload documents says
                        // thrown == null means the work COMPLETED. The caller would read a refusal.
                        //
                        // The trigger is the same one Enqueue's own remarks name: the retry reaches
                        // world.SaveBiota -> SerializedShardDatabase._queue.Add, and BlockingCollection
                        // .Add throws once CompleteAdding() has run at shutdown. Today's defaults happen
                        // to look like a refusal rather than a success, so this was safe by accident;
                        // the guarantee is what is being fixed, not an observed symptom.
                        RetryPendingDepositSavesGuarded();

                        next();
                    }
                    catch (Exception ex)
                    {
                        // A throw must not strand the queue or the drain lock. Everything the store
                        // itself does is already guarded; this catches a caller's closure.
                        log.Error($"[VAULT] account {AccountId}: queued vault work threw: {ex.GetFullMessage()}");
                    }
                    finally
                    {
                        mutationThreadId = previous;
                    }
                }
            }
        }

        /// <summary>
        /// Re-attempts any deposit save that reported failure. Called from <see cref="Drain"/> ahead of
        /// each dequeued item, and from <see cref="TryEvict"/> before an otherwise-idle store is
        /// retired. Both callers already hold <see cref="drainLock"/>, and that is the exclusion this
        /// actually rests on rather than the mutation queue as such - the queue is drained under that
        /// same lock, so no deposit or withdrawal can be running while this does.
        ///
        /// Gives up after MaxDepositSaveAttempts and leaves a loud line naming the guid, because at that
        /// point only manual recovery can help. That give-up is also what keeps TryEvict's guard from
        /// pinning a store forever: the list empties after a bounded number of sweeps either way.
        /// </summary>
        /// <summary>
        /// <see cref="RetryPendingDepositSaves"/> with its own catch (review round, finding 1).
        ///
        /// ONE wrapper shared by both call sites rather than a try/catch copied into each, because the
        /// two sites fail differently and would drift: in <see cref="Drain"/> a throw silently skips
        /// somebody else's queued work, and in <see cref="TryEvict"/> it escapes into
        /// AccountVaultManager.Tick's unguarded sweep. Neither is a reason to abandon the retry - a
        /// pass that could not run is simply a pass skipped, and the item stays in
        /// <see cref="pendingDepositSaves"/> for the next one, with its attempt count already spent so
        /// the give-up bound still holds.
        /// </summary>
        private void RetryPendingDepositSavesGuarded()
        {
            try
            {
                RetryPendingDepositSaves();
            }
            catch (Exception ex)
            {
                log.Error($"[VAULT] account {AccountId}: RetryPendingDepositSaves threw and was skipped this pass: {ex.GetFullMessage()}");
            }
        }

        private void RetryPendingDepositSaves()
        {
            List<WorldObject> due;

            lock (stateLock)
            {
                if (pendingDepositSaves.Count == 0)
                    return;

                due = pendingDepositSaves.Keys.ToList();
            }

            foreach (var item in due)
            {
                int attempts;

                lock (stateLock)
                {
                    if (!pendingDepositSaves.TryGetValue(item, out attempts))
                        continue;

                    attempts++;

                    if (attempts > MaxDepositSaveAttempts)
                    {
                        pendingDepositSaves.Remove(item);
                        log.Error($"[VAULT] account {AccountId}: gave up re-saving vault item 0x{item.Guid.Full:X8} after {MaxDepositSaveAttempts} attempts. Its biota row still points at wherever it was before the deposit or barreling, so the item will not survive a restart in the container it is in now. Manual recovery required.");
                        continue;
                    }

                    pendingDepositSaves[item] = attempts;
                }

                // Outside stateLock. The test world source answers the callback INLINE on this thread,
                // and the callback takes stateLock itself; calling in from under the lock would rely on
                // Monitor reentrancy rather than on the leaf-lock discipline the class documents.
                world.SaveBiota(item, saved =>
                {
                    if (!saved)
                        return;

                    lock (stateLock)
                        pendingDepositSaves.Remove(item);
                });
            }
        }

        private bool RunOnQueue(Action work)
        {
            if (IsOnMutationQueue)
            {
                work();
                return true;
            }

            return Enqueue(work);
        }

        private void AssertOnMutationQueue(string caller)
        {
            if (IsOnMutationQueue)
                return;

            throw new InvalidOperationException(
                $"AccountVaultStore.{caller} must run on the store's mutation queue: call it inside " +
                $"store.Enqueue(...). Running it off the queue would leave the biota path unserialized, " +
                $"which is exactly the two-windows race (R3) the queue exists to close.");
        }

        private void Touch()
        {
            LastTouchedUnixTime = Time.GetUnixTime();
        }

        #endregion

        #region Mule form

        /// <summary>
        /// The account's saved mule form, cached for the store's lifetime. Null until the first read;
        /// see <see cref="muleFormLoaded"/> for why null is not enough on its own.
        ///
        /// Guarded by <see cref="stateLock"/> like every other cached field on this store.
        /// </summary>
        private MuleFormLook? muleForm;

        /// <summary>
        /// Whether the form has been read from the backend yet. Needed separately from
        /// <see cref="muleForm"/> because "no saved look" is the ordinary answer for most accounts and
        /// is itself worth caching - without this flag every summon on such an account would put
        /// another read on the database.
        /// </summary>
        private bool muleFormLoaded;

        /// <summary>
        /// The account's saved mule form, or null for "wear the default look".
        ///
        /// DELIBERATELY NOT ROUTED THROUGH <see cref="TryEnsureLoadedLocked"/>,
        /// <see cref="CheckReadyLocked"/> OR <see cref="IsLoaded"/>. All three refuse the whole store
        /// when the vault index read fails, which is correct for items - serving a short vault list
        /// loses them - and wrong for a cosmetic. A stale, missing or unreadable form must degrade to
        /// the default look and must never be able to refuse a summon
        /// (Docs/MuleVendor/2026-09-01-mule-form-token-design.md sections 6 and 8), so the form load
        /// is a second, independent lazy load that shares nothing with the index load but the lock.
        /// </summary>
        internal MuleFormLook? GetMuleForm()
        {
            lock (stateLock)
            {
                EnsureMuleFormLoadedLocked();

                return muleForm;
            }
        }

        /// <summary>
        /// Reads the form once per store. The backend call happens WITH <see cref="stateLock"/> HELD,
        /// which is the same choice <see cref="TryEnsureLoadedLocked"/> makes for the index read and is
        /// safe for the same reason: the backend is a leaf, so nothing it calls re-enters this store.
        ///
        /// A FAILED read is cached as "no look" for the store's lifetime, logged once. That is the
        /// design's explicit ruling (section 8): a cosmetic must never put a retry loop on the summon
        /// path, and a later successful <see cref="TrySetMuleForm"/> repopulates the cache. The flag is
        /// set BEFORE the read so a failure cannot leave the store re-reading on every summon.
        /// </summary>
        private void EnsureMuleFormLoadedLocked()
        {
            if (muleFormLoaded)
                return;

            muleFormLoaded = true;

            var (ok, row) = backend.GetAccountMuleForm(AccountId);

            if (!ok)
            {
                log.Error($"[MULEFORM] account {AccountId}: could not read the saved mule form. Using the default look for the lifetime of this store; a summon is never refused over a cosmetic.");
                muleForm = null;
                return;
            }

            muleForm = row == null ? (MuleFormLook?)null : new MuleFormLook(row.FormWcid, row.FormName);
        }

        /// <summary>
        /// Saves a new look for the account and replaces the cache.
        ///
        /// The caller is always acting on its OWN account's store (the token's no-target path resolves
        /// the store from the using player's account), so there is no ownership check here and no
        /// refusal string beyond <see cref="UnavailableMessage"/>. A share grantee never reaches this:
        /// sharing grants access to items, not to the owner's appearance.
        /// </summary>
        internal bool TrySetMuleForm(uint wcid, string formName, VaultActor actor, out string failReason)
        {
            var ok = false;
            string reason = null;

            // RunOnQueue returning false means the work never ran at all, so `reason` is still null and
            // has to be filled in here - the same shape TryGrant uses.
            if (!RunOnQueue(() => ok = TrySetMuleFormCore(wcid, formName, actor, out reason)))
                reason = UnavailableMessage;

            failReason = reason;
            return ok;
        }

        /// <summary>
        /// MUST run on the mutation queue, like every other store mutation.
        ///
        /// Does NOT bump <see cref="Version"/>. The version stamp gates PersonalVendor's item-panel
        /// rebuild; a look change is not an inventory change, and bumping it would rebuild every open
        /// vendor window for a cosmetic.
        /// </summary>
        internal bool TrySetMuleFormCore(uint wcid, string formName, VaultActor actor, out string failReason)
        {
            AssertOnMutationQueue(nameof(TrySetMuleFormCore));

            Touch();

            failReason = null;

            // form_Name is varchar(64) and is a snapshot nothing keys on, so a long donor name is
            // trimmed here rather than allowed to fail the write and cost the player their look.
            var name = formName ?? string.Empty;

            if (name.Length > MuleFormNameMaxLength)
                name = name.Substring(0, MuleFormNameMaxLength);

            var row = new AccountMuleForm
            {
                AccountId = AccountId,
                FormWcid = wcid,
                FormName = name,
                SetByCharacterGuid = actor.CharacterGuid,
                SetUnixTime = Time.GetUnixTime(),
            };

            if (!backend.UpsertAccountMuleForm(row))
            {
                failReason = UnavailableMessage;
                return false;
            }

            lock (stateLock)
            {
                muleForm = new MuleFormLook(wcid, name);
                muleFormLoaded = true;
            }

            return true;
        }

        /// <summary>Matches account_mule_form.form_Name's varchar(64).</summary>
        private const int MuleFormNameMaxLength = 64;

        #endregion

        #region Vendor windows

        /// <summary>How many vendor windows are currently open onto this store.</summary>
        public int OpenWindows => Volatile.Read(ref openWindows);

        public void AddWindow()
        {
            Interlocked.Increment(ref openWindows);
            Touch();
        }

        public void RemoveWindow()
        {
            // Clamped: a double-close must not drive the count negative and keep the store pinned in
            // memory forever.
            while (true)
            {
                var current = Volatile.Read(ref openWindows);

                if (current <= 0)
                    return;

                if (Interlocked.CompareExchange(ref openWindows, current - 1, current) == current)
                    break;
            }

            Touch();
        }

        #endregion

        #region Reads

        /// <summary>
        /// One page of the store's contents: stored biotas first, in vault order and then placement
        /// order, followed by the non-zero ledger rows ordered by wcid. The ordering is fixed so that
        /// a page boundary means the same thing on two consecutive calls WITHIN A SESSION - and only
        /// within one. PlacementPosition is renumbered in memory by TryAddToInventory
        /// (Container.cs:566-569) while only the added item and the container are saved
        /// (Container.cs:778-779), and SortWorldObjectsIntoInventory re-normalizes the whole container
        /// on load (Container.cs:175-180), so the same vault can present a different order after a
        /// restart. Deterministic, not stable.
        ///
        /// Paged from day one (invariant 1). v1 asks for the full range; if live test 17.1 comes back
        /// with a low client ceiling, paging becomes a change to the vendor view and to
        /// account_vault_entry_cap, not to this signature.
        ///
        /// Returns an EMPTY list - never a partial one - when the store is not loaded (R4). Callers
        /// that need to tell the player why check <see cref="IsLoaded"/> and send
        /// <see cref="StillLoadingMessage"/> or <see cref="UnavailableMessage"/>.
        /// </summary>
        public IReadOnlyList<VaultEntry> GetEntries(int offset, int limit)
        {
            Touch();

            var entries = new List<VaultEntry>();

            if (limit == 0)
                return entries;

            lock (stateLock)
            {
                if (!CheckReadyLocked(out _))
                    return entries;

                if (offset < 0)
                    offset = 0;

                var skipped = 0;

                foreach (var entry in EnumerateEntriesLocked())
                {
                    if (skipped < offset)
                    {
                        skipped++;
                        continue;
                    }

                    entries.Add(entry);

                    // A negative limit means "everything from the offset on".
                    if (limit > 0 && entries.Count >= limit)
                        break;
                }
            }

            return entries;
        }

        /// <summary>
        /// Stored biotas - bucketed into group rows where the 2026-08-31 grouping design says they are
        /// equivalent - followed by the non-zero ledger rows.
        ///
        /// A one-member group is emitted as an ORDINARY item entry rather than a group of one, which is
        /// what keeps every existing behaviour of a lone stored item exactly as it was: its Count is
        /// still its own StackSize, and a partial withdraw of it is still refused.
        /// </summary>
        private IEnumerable<VaultEntry> EnumerateEntriesLocked()
        {
            foreach (var group in GroupedStoredItemsLocked())
                yield return group.Count > 1 ? VaultEntry.ForGroup(group) : VaultEntry.ForItem(group[0]);

            foreach (var kvp in ledger.Where(k => k.Value > 0).OrderBy(k => k.Key))
                yield return VaultEntry.ForLedger(kvp.Key, kvp.Value);
        }

        /// <summary>
        /// The account's sharing grants, or NULL if the read failed. Null is not "no grants".
        /// </summary>
        public List<AccountVaultGrant> GetGrants()
        {
            Touch();

            return backend.GetAccountVaultGrants(AccountId);
        }

        /// <summary>
        /// The most recent audit rows, newest first, or NULL if the read failed.
        /// </summary>
        public List<AccountVaultLog> GetRecentLog(int limit)
        {
            Touch();

            return backend.GetAccountVaultLog(AccountId, limit);
        }

        #endregion

        #region Authorization (DESIGN section 10)

        /// <summary>
        /// THE single way to resolve vault access. Returns false ONLY when the grant read itself
        /// failed, with failReason set to UnavailableMessage; a genuine "no grant" returns true with
        /// access == VaultAccess.None. Keeping those two apart is the whole point of the signature.
        ///
        /// There used to be a collapsing GetAccess convenience overload beside this one, returning
        /// VaultAccess.None for both cases. Three of five production authorization sites took it, and
        /// during a database outage told players they lacked permission rather than that the vault was
        /// unavailable - the most alarming thing this feature can say to someone with items stored.
        /// The reconciliation pass in Task 12 moved every site here and DELETED the overload, so that
        /// mistake is now impossible rather than merely documented. Do not reintroduce a bool-discarding
        /// wrapper: production code has no legitimate use for one, and test code that wants the terse
        /// form has a local helper (AccountVaultStoreTests.AccessOf).
        ///
        /// Re-resolved on EVERY call, with no caching anywhere. DESIGN section 10 requires a revoke to
        /// take effect immediately with no session-invalidation logic, and TryWithdraw re-runs this
        /// unconditionally inside every transaction independent of whether any UX-level gate ran,
        /// because the transaction handlers resolve the vendor purely by guid off the landblock and
        /// never verify that a panel was ever opened.
        /// </summary>
        internal bool TryGetAccess(VaultActor actor, out VaultAccess access, out string failReason)
        {
            Touch();

            access = VaultAccess.None;
            failReason = null;

            if (actor.CharacterGuid == 0)
            {
                failReason = "You do not have permission to use this vault.";
                return false;
            }

            // Account id 0 means the actor's account could not be resolved, and never matches a real
            // account, so an unresolved actor is denied rather than treated as the owner.
            if (actor.AccountId != 0 && actor.AccountId == AccountId)
            {
                access = VaultAccess.DepositWithdraw;
                return true;
            }

            var grants = backend.GetAccountVaultGrants(AccountId);

            if (grants == null)
            {
                // The read failed. Refusing is the fail-safe direction; treating it as "no grants"
                // would be too, but the caller deserves the right message.
                failReason = UnavailableMessage;
                return false;
            }

            var grant = grants.FirstOrDefault(g => g.GranteeCharacterGuid == actor.CharacterGuid);

            if (grant == null)
                return true;    // access stays None

            access = grant.CanWithdraw ? VaultAccess.DepositWithdraw : VaultAccess.Deposit;
            return true;
        }

        #endregion

        #region Deposit

        public bool TryDeposit(WorldObject item, Player actor, out string failReason)
        {
            return TryDeposit(item, VaultActor.From(actor), out failReason);
        }

        /// <summary>
        /// Stores one item. The ORDER of the steps below is the R5 mitigation and is not stylistic:
        /// ledger first, destroy second, because a dupe is recoverable from the audit log and a
        /// vanished item is not.
        ///
        /// The caller must already have detached the item from the actor (the vendor sell path does
        /// this before it gets here). An item still parented to a player would end up in two
        /// containers at once, so it is refused rather than accepted.
        /// </summary>
        internal bool TryDeposit(WorldObject item, VaultActor actor, out string failReason)
        {
            AssertOnMutationQueue(nameof(TryDeposit));

            Touch();

            failReason = null;

            if (item == null)
            {
                failReason = "There is nothing to store.";
                return false;
            }

            lock (stateLock)
            {
                if (!CheckReadyLocked(out failReason))
                    return false;
            }

            if (!TryGetAccess(actor, out var access, out failReason))
                return false;

            if (access == VaultAccess.None)
            {
                failReason = "You do not have permission to use this vault.";
                return false;
            }

            if (item.UseBackpackSlot)
            {
                // UseBackpackSlot is WeenieType == Container OR RequiresPackSlot
                // (WorldObject_Properties.cs:2070, :1813), and the two are refused for different
                // reasons, so they must not share a message. A Focusing Stone occupies a pack slot
                // without being a pack, and telling its owner to "store what is inside it" is advice
                // they cannot act on.
                //
                // Vault containers pin ContainerCapacity to 0, so TryAddToInventory would refuse a pack
                // anyway. Refusing here instead gives the player the real reason rather than the
                // generic "unavailable", and it is where the rule is decided rather than where it
                // happens to be enforced.
                failReason = item.WeenieType == WeenieType.Container
                    ? "A pack cannot be stored in your vault. Store what is inside it instead."
                    : "That takes up a pack slot of its own and cannot be stored in your vault.";

                return false;
            }

            if (item.Lifespan != null)
            {
                // Fix round 2, F3: a TIMED item must never enter the vault, and the reason is the
                // collapse rule rather than anything about storage.
                //
                // WorldObject_Tick computes expiry as CreationTimestamp + Lifespan, and VaultCollapse
                // deliberately ignores CreationTimestamp when it diffs a candidate against a fresh
                // weenie (it is "when it was made", not what the item is). So a template-identical
                // timed stackable is PRISTINE: it collapses to a wcid ledger row, its biota is
                // destroyed, and the withdrawal builds a brand new object whose CreationTimestamp is
                // now. The clock restarts. Vault containers are also never ticked, so a stored one does
                // not expire while it sits there either.
                //
                // Refused here rather than "fixed" in VaultCollapse: diffing on CreationTimestamp would
                // make every ordinary stack unique to its own creation moment and stop the ledger
                // firing at all (DESIGN section 8 is explicit that collapse is a diff, not a
                // blacklist, and that exemption is what makes it work). The refusal is a rule about
                // what may be STORED, which is this method's business, and it is mirrored verbatim in
                // PersonalVendor.CanAcceptCore so the pre-flight and the store never disagree.
                failReason = "A timed item cannot be stored in your vault.";
                return false;
            }

            if (item.ItemType == ItemType.PromissoryNote)
            {
                // A trade note never becomes vault content: PersonalVendor.DepositItems diverts it to
                // the player's banked pyreals at face value (Player.BankTradeNote) before it is ever
                // offered to a store, so the vendor path does not reach this line at all. It is here as
                // defence in depth for any OTHER caller of TryDeposit, present or future.
                //
                // Why a note must not be stored: withdrawal is free on the server, but the client's
                // vendor panel gates every row on the player being able to afford that row's Value in
                // coin, and the client is never patched. A stored Trade Note (250,000) is therefore
                // unrecoverable by anyone not already carrying 250,000 pyreals - the item is in the
                // vault and the player cannot take it out. Banking it is exact (item.Value is the whole
                // stack's value) and leaves the same purchasing power withdrawable at will.
                //
                // THIS IS THE ONE STORE REFUSAL DELIBERATELY NOT MIRRORED IN
                // PersonalVendor.CanAcceptCore. Every other refusal here is mirrored there so an item
                // is never detached from the player for a deposit the store will reject. A note must
                // do the opposite: it has to PASS the pre-flight to reach the sell list, because being
                // banked is the outcome the player wants. A copy of this guard in CanAcceptCore would
                // refuse the note at the panel and the divert would become dead code.
                failReason = "Trade notes are banked, not stored.";
                return false;
            }

            if (item.ContainerId != null || item.WielderId != null)
            {
                log.Error($"[VAULT] account {AccountId}: refused a deposit of 0x{item.Guid.Full:X8} because it is still parented (container {item.ContainerId}, wielder {item.WielderId}). The caller must detach it first.");
                failReason = UnavailableMessage;
                return false;
            }

            // OUTSIDE every lock, deliberately. IsPristine takes the item's own BiotaDatabaseLock read
            // lock, and BiotaDatabaseLock is a default-policy ReaderWriterLockSlim (NoRecursion), so a
            // caller already holding that item's lock would make the acquisition throw, have it
            // swallowed by IsPristine's own catch, and get false back on every single call - a silent,
            // total failure of the ledger that presents as "nothing ever collapses".
            //
            // It runs BEFORE the cap check because the cap check needs its answer, and moving it up is
            // the only thing that changed - it is still outside every lock in this class and the store
            // still never takes a BiotaDatabaseLock.
            var pristine = world.IsPristine(item);

            lock (stateLock)
            {
                // The cap counts ENTRIES, not units (DESIGN 7.3), so it may only refuse a deposit that
                // actually ADDS one. A pristine item topping up a ledger row the account already holds
                // creates no row and no entry, and refusing it is the cap doing the opposite of what
                // 7.3 says it is for: an account at 500 of 500 with 9,000 healing kits on one row was
                // being told its vault was full for the 9,001st, which is exactly the hoarding case
                // collapse exists to make free. The rule itself is AddsEntryLocked, shared with
                // WouldAddEntry so the two cannot drift apart again.
                var addsEntry = AddsEntryLocked(pristine, item);

                if (addsEntry && !HasRoomForLocked(1))
                {
                    var count = EntryCountLocked();
                    var cap = EffectiveEntryCap;
                    failReason = $"{FullMessagePrefix} {count} of {cap} entries. Withdraw something before storing more.";
                    return false;
                }
            }

            // The version bump lives INSIDE each of these two, immediately after the mutation it
            // covers, rather than out here on the success return. Both commit their change and then run
            // a tail that can throw - DepositToLedger credits the ledger and then calls
            // world.DestroyItem, DepositToVault adds to the vault container and then saves - and a throw
            // from either unwinds straight out of this method, past any bump placed here. Drain catches
            // it and does not rethrow, so the window would keep showing pre-deposit contents with no
            // later mutation to correct it. This is the same reasoning TryWithdraw's unconditional bump
            // already applies to its own half-applied failure path.
            return pristine
                ? DepositToLedger(item, actor, out failReason)
                : DepositToVault(item, actor, out failReason);
        }

        private bool DepositToLedger(WorldObject item, VaultActor actor, out string failReason)
        {
            failReason = null;

            var wcid = item.WeenieClassId;
            var name = item.Name;
            var units = item.StackSize ?? 1;

            if (units < 1)
                units = 1;

            // Ledger FIRST. If this is REFUSED, nothing has happened and the player keeps the item.
            var credit = backend.TryAdjustAccountVaultStack(AccountId, wcid, units, out var newCount);

            if (credit == AccountVaultStackAdjustResult.Refused)
            {
                log.Error($"[VAULT] account {AccountId}: the ledger refused a deposit of {units} x wcid {wcid}. The item was NOT destroyed.");
                failReason = UnavailableMessage;
                return false;
            }

            // THE DEPOSIT/WITHDRAW ASYMMETRY (fix round 2, F1). Read Failed as "nobody knows whether
            // the credit landed", never as "it did not".
            //
            // A deposit and a withdraw resolve that unknown in OPPOSITE directions, and the reason is
            // that the two have different worst cases, not that one path is more careful than the
            // other:
            //
            //   - DEPOSIT: the item still exists in the player's hands. Destroying it on a credit that
            //     may never have landed is a certain, unrecoverable loss. Keeping it when the credit
            //     DID land is a duplicate - visible in account_vault_log beside a ledger row an
            //     operator can reconcile. A visible dupe beats a silent loss, so the item is kept and
            //     nothing is destroyed.
            //   - WITHDRAW (see WithdrawFromLedger): the debit may already have taken the units, and
            //     the player has nothing. Refusing there would write a balancing Return row over a
            //     debit that really happened, which erases the loss from the audit trail entirely - so
            //     that path proceeds instead.
            //
            // Both log the same [VAULT] LEDGER STATE UNKNOWN marker, because both need an operator to
            // look, and the marker is what makes the two reconcilable against each other.
            if (credit == AccountVaultStackAdjustResult.Failed)
            {
                log.Error($"[VAULT] LEDGER STATE UNKNOWN: account {AccountId}, wcid {wcid}, delta +{units}. A deposit credit neither committed nor provably failed. The item 0x{item.Guid.Full:X8} was NOT destroyed and the player keeps it - if the credit DID land, this account now holds those units twice and needs reconciling against account_vault_log.");

                InvalidateLedger($"an unknown-outcome deposit credit of {units} x wcid {wcid}");

                failReason = UnavailableMessage;
                return false;
            }

            if (credit == AccountVaultStackAdjustResult.AppliedCountUnknown)
            {
                // The units ARE in the ledger; only the resulting count is unknown. Proceed with the
                // deposit exactly as for Applied, but re-read the ledger rather than writing a count
                // that would be a guess.
                InvalidateLedger($"a deposit credit of {units} x wcid {wcid} whose count could not be read back");
            }
            else
            {
                lock (stateLock)
                {
                    if (newCount > 0)
                        ledger[wcid] = newCount;
                    else
                        ledger.Remove(wcid);
                }
            }

            // Bumped HERE, the moment the credit is committed, and not on TryDeposit's success return:
            // DestroyItem below can throw, and a throw unwinds past any bump placed after this call.
            Interlocked.Increment(ref version);

            // Destroy SECOND. The reverse order loses the item on a crash.
            world.DestroyItem(item);

            WriteLog(AccountVaultAction.Deposit, actor, wcid, null, name, units);

            return true;
        }

        private bool DepositToVault(WorldObject item, VaultActor actor, out string failReason)
        {
            failReason = null;

            var vault = FindOrCreateVault();

            if (vault == null)
            {
                failReason = UnavailableMessage;
                return false;
            }

            lock (stateLock)
            {
                if (!vault.TryAddToInventory(item))
                {
                    log.Error($"[VAULT] account {AccountId}: TryAddToInventory refused 0x{item.Guid.Full:X8} on vault 0x{vault.Guid.Full:X8} ({vault.Inventory.Count} of {vault.ItemCapacity} slots used).");
                    failReason = UnavailableMessage;
                    return false;
                }

                // INSIDE the lock, beside the mutation, not after it. The version bump below happens
                // outside, so a reader taking stateLock between the two would otherwise see this item
                // in the vault while the memo still described the vault without it.
                InvalidateGroupingLocked();
            }

            // Bumped HERE, the moment the item is physically in the vault, and not on TryDeposit's
            // success return: everything below can throw, and a throw unwinds past any bump placed
            // after it.
            Interlocked.Increment(ref version);

            // Save FIRST, so the item's new ContainerId is persisted. The caller already detached it
            // from the actor, so until this lands the row points at nothing and the orphan purge would
            // be entitled to it (R1). The container save copies Storage.OnAddItem (Storage.cs:80-91),
            // which exists precisely to prevent item loss and is not optional here.
            // BOTH captured before the callback is built, not just the guid. The callback arrives on the
            // database worker thread, NOT the mutation queue - it may only take stateLock and log - and
            // reading item.WeenieClassId from inside it was an unsynchronized property read of an object
            // this thread no longer owns, two tokens after the capture that exists to avoid exactly
            // that. `item` itself is still used inside, but only as a dictionary key: its identity, not
            // its state.
            var depositedGuid = item.Guid.Full;
            var depositedWcid = item.WeenieClassId;

            world.SaveBiota(item, saved =>
            {
                if (saved)
                    return;

                log.Error($"[VAULT] DEPOSIT SAVE FAILED for account {AccountId}: 0x{depositedGuid:X8} (wcid {depositedWcid}) is in the vault in memory but its biota did not persist. Queued for retry.");

                lock (stateLock)
                {
                    if (!pendingDepositSaves.ContainsKey(item))
                        pendingDepositSaves[item] = 0;
                }
            });

            world.SaveBiota(vault);

            WriteLog(AccountVaultAction.Deposit, actor, item.WeenieClassId, item.Guid.Full, item.Name, item.StackSize ?? 1);

            return true;
        }

        /// <summary>
        /// DESIGN 7.1: vaults are a SET, not a chain. Scan oldest-first and take the first with a free
        /// slot, so fragmentation from withdrawals is structurally harmless and reaping is never
        /// needed to reclaim it.
        /// </summary>
        private Container FindVaultWithFreeSlotLocked()
        {
            foreach (var vault in vaults)
            {
                if (vault.Inventory.Count < (vault.ItemCapacity ?? 0))
                    return vault;
            }

            return null;
        }

        /// <summary>
        /// The first vault with a free slot, creating one when every vault is full.
        ///
        /// Safe to run without holding <see cref="stateLock"/> across the whole thing because it only
        /// ever runs on the mutation queue, and the queue is what serializes vault creation. Reads
        /// from other threads take the lock, but no read adds or removes a vault. Splitting it this way
        /// is what keeps <see cref="CreateVault"/>'s blocking wait for the biota save out of a lock
        /// that a vendor panel's read would otherwise sit behind.
        /// </summary>
        private Container FindOrCreateVault()
        {
            lock (stateLock)
            {
                var existing = FindVaultWithFreeSlotLocked();

                if (existing != null)
                    return existing;
            }

            return CreateVault();
        }

        /// <summary>
        /// Builds a new vault container, persists it, and registers its index row - IN THAT ORDER, and
        /// the order is the whole point.
        ///
        /// The container biota save and the account_vault insert are two separate writes to two
        /// different code paths: the save is a fire-and-forget enqueue onto SerializedShardDatabase's
        /// worker thread (WorldObject_Database.cs:61-65), the insert is a synchronous SaveChanges
        /// (ShardDatabase_AccountVault.cs:85-90). Registering first means a crash, a docker stop or a
        /// deploy landing between them leaves an index row pointing at a biota that never existed - and
        /// that dangling row used to refuse the WHOLE store forever, taking every other vault on the
        /// account with it. Waiting for the save callback closes the window, which is what DESIGN R5
        /// means by "persisted means the callback fired, not the call returned".
        ///
        /// The recovery path in <see cref="TryEnsureLoadedLocked"/> is the second half of the same fix
        /// and is not made redundant by this one: this narrows the window, that one survives it.
        /// </summary>
        private Container CreateVault()
        {
            var container = CreateIndexedContainer(VaultContainerKind, VaultContainerName, out var rowId);

            if (container == null)
                return null;

            lock (stateLock)
            {
                vaults.Add(container);
                vaultRowIds[container.Guid.Full] = rowId;

                // A new (empty) vault changes nothing groupable today, but it changes what
                // EnumerateStoredItemsLocked walks, and the memo must never outlive the shape it was
                // built from.
                InvalidateGroupingLocked();
            }

            return container;
        }

        /// <summary>
        /// The account's barrel container, created on the first barreling and reused forever after.
        /// Null when it does not exist and could not be created.
        ///
        /// Runs on the mutation queue like <see cref="CreateVault"/>, and for the same reason: the
        /// queue is what makes "check whether it exists, then create it" atomic, so two concurrent
        /// barrelings cannot each create one.
        /// </summary>
        private Container FindOrCreateBarrel()
        {
            lock (stateLock)
            {
                if (barrel != null)
                    return barrel;
            }

            var container = CreateIndexedContainer(BarrelContainerKind, BarrelContainerName, out var rowId);

            if (container == null)
                return null;

            lock (stateLock)
            {
                barrel = container;
                barrelRowId = rowId;

                // Deliberately NO InvalidateGroupingLocked here, and the omission is not an oversight:
                // grouping walks `vaults`, the barrel is not in it, and the memo's own structural
                // checks count vaults and stored items in vaults. A barrel appearing changes neither.
            }

            return container;
        }

        /// <summary>
        /// Builds one hidden container of <paramref name="kind"/>, persists it, and registers its
        /// index row - IN THAT ORDER. Extracted from <see cref="CreateVault"/> when the barrel became
        /// a second kind of container, so the two cannot drift: every step below exists to prevent an
        /// orphaned or world-visible container, and a second hand-written copy of the sequence would
        /// eventually be missing one of them.
        ///
        /// The caller does its own bookkeeping (adding to <see cref="vaults"/>, or assigning
        /// <see cref="barrel"/>) because that is exactly what differs between the two.
        ///
        /// See <see cref="CreateVault"/>'s remarks, which this inherits in full, for why the save must
        /// be confirmed BEFORE the index row is written and why the container is destroyed on either
        /// failure.
        /// </summary>
        private Container CreateIndexedContainer(int kind, string name, out uint rowId)
        {
            rowId = 0;

            var container = world.CreateNewWorldObject(VaultContainerWcid) as Container;

            if (container == null)
            {
                log.Error($"[VAULT] account {AccountId}: could not instantiate a container from wcid {VaultContainerWcid} (kind {kind}).");
                return null;
            }

            // R7. Set in code REGARDLESS of what the weenie says, because ItemCapacity is a (byte?)
            // cast and a data value above 255 wraps silently.
            container.ItemCapacity = VaultItemCapacity;

            // No nested packs. Keeps one vault's Inventory.Count equal to its entry count exactly, and
            // keeps the store clear of a second level of async inventory loading (R4) and of children
            // that a reap could orphan (R2).
            container.ContainerCapacity = 0;

            container.Name = name;

            // Mitigation 1 of DESIGN 7.2 (risk R1). Assigned HERE, before the save below, or the saved
            // biota carries no Location and the guard exists only in memory. See
            // ReservedVaultLocation for what this Location is and is not.
            container.Location = ReservedVaultLocation();

            if (!SaveAndWait(container))
            {
                // Nothing was registered, so the container is unreachable - and provably empty, since
                // it has existed only inside this method. Destroying it here is the one moment that is
                // safe (R2), and it is the same rollback the failed-registration path below performs.
                log.Error($"[VAULT] account {AccountId}: the biota save for new container 0x{container.Guid.Full:X8} (kind {kind}) did not land; destroying it rather than registering an index row for a biota that may not exist.");
                world.DestroyItem(container);
                return null;
            }

            var row = new ShardAccountVault
            {
                AccountId = AccountId,
                ContainerGuid = container.Guid.Full,
                CreatedAt = DateTime.UtcNow,
                Kind = kind,
            };

            if (!backend.AddAccountVault(row))
            {
                // The index row is what makes the container findable again. Without it the biota is an
                // orphan that no future load would ever look for, so destroy it now while it is
                // provably empty - the one moment when destroying a vault container is safe.
                log.Error($"[VAULT] account {AccountId}: could not register container 0x{container.Guid.Full:X8} (kind {kind}); destroying it rather than leaving an unreachable biota.");
                world.DestroyItem(container);
                return null;
            }

            // R1, second half. Registered only once the index row landed: before that the container is
            // still rolled back and destroyed on failure, and a registry entry for a destroyed guid
            // would suppress whatever recycled that guid later.
            AccountVaultSpawnFilter.Register(container.Guid.Full);

            rowId = row.Id;

            return container;
        }

        /// <summary>
        /// The reserved-landblock Location every vault container carries. Mitigation 1 of DESIGN 7.2
        /// (risk R1).
        ///
        /// This is NOT a placement, and it reads as a bug to anyone who has not read DESIGN 7.2. The
        /// container never enters a landblock, is never ticked, is never sent to a client and is never
        /// handled by a player. The Location exists for exactly one reason: PurgeOrphanedBiotas keeps a
        /// biota only if it has a Container, Wielder or Location pointer
        /// (ShardDatabaseOfflineTools.PurgeOrphanedBiotasInParallel), a vault container has neither of
        /// the first two, and the same purge then deletes children whose parent container is missing -
        /// so a dropped vault takes every stored item in it with it, catastrophically and with no
        /// recovery.
        ///
        /// The keep-test exemption in ShardDatabaseOfflineTools is the redundant SECOND half of the
        /// same mitigation. This one can be undone by a careless content edit, that one by a merge from
        /// upstream, so neither may be removed as "already covered by the other".
        ///
        /// account_vault_landblock names a landblock that is never activated for players and holds no
        /// content. Nothing in the engine ENFORCES that, which is why
        /// <see cref="AccountVaultSpawnFilter"/> exists and why a configured value other than the
        /// registered default is logged here at ERROR rather than merely accepted.
        ///
        /// Cell is pinned to 0x0001 rather than left at 0x0000. Leaving it 0 is not harmful: the
        /// Position constructor calls SetPosition when the low word is zero, and at x=y=0 that
        /// transitions no landblock and SetLandCell preserves the high word outright
        /// (Position.cs:225), so it derives the same landblock and the same cell 0x0001. Pinning it is
        /// for the reader and for the stored row - the persisted ObjCellId is then exactly the value
        /// this method asked for rather than one the constructor happened to derive, and it stays that
        /// way if the constructor's derivation ever changes.
        /// </summary>
        internal static Position ReservedVaultLocation()
        {
            var configured = (ushort)(PropertyManager.GetLong("account_vault_landblock").Item & 0xFFFF);
            var registered = RegisteredVaultLandblock();

            // A configured 0 is refused rather than used: PropertyManager.GetLong falls back to 0 for a
            // key it can neither find in its cache nor read from the shard, and landblock 0x0000 is a
            // REAL landblock - taking that 0 at face value would put every vault container somewhere a
            // player can walk. This is load-bearing in CI, where MySQL is present but the config row is
            // not, so GetLong caches the 0 fallback.
            if (configured == 0)
                log.Error($"[VAULT] account_vault_landblock read as 0, which is a real landblock. Using the registered default 0x{registered:X4} instead.");
            else if (configured != registered)
                log.Error($"[VAULT] account_vault_landblock is configured as 0x{configured:X4}, not the reserved default 0x{registered:X4}. Every vault container biota will carry a Location in 0x{configured:X4}, and if anything ever activates that landblock the only thing keeping every account's vault out of the live world is AccountVaultSpawnFilter. Set it back unless this was deliberate.");

            var landblock = (uint)ConfiguredVaultLandblock();

            return new Position((landblock << 16) | 0x0001, 0f, 0f, 0f, 0f, 0f, 0f, 1f, 0u);
        }

        /// <summary>
        /// account_vault_landblock masked to 16 bits, with a configured 0 replaced by the registered
        /// default. Quiet - <see cref="ReservedVaultLocation"/> does the logging, because this is also
        /// read once per landblock activation and an ERROR there would be a log flood rather than a
        /// signal.
        /// </summary>
        internal static ushort ConfiguredVaultLandblock()
        {
            var configured = (ushort)(PropertyManager.GetLong("account_vault_landblock").Item & 0xFFFF);

            return configured != 0 ? configured : RegisteredVaultLandblock();
        }

        /// <summary>
        /// The value account_vault_landblock ships with, or 0 if the key has been removed from
        /// DefaultLongProperties. Read back out of the registry rather than restated here, so the
        /// number lives in exactly one place (PropertyManager's DefaultLongProperties).
        /// </summary>
        internal static ushort RegisteredVaultLandblock()
        {
            if (DefaultPropertyManager.DefaultLongProperties.TryGetValue("account_vault_landblock", out var registered))
                return (ushort)(registered.Item & 0xFFFF);

            return 0;
        }

        /// <summary>
        /// Saves a biota and does not return until the save has actually landed, or until
        /// <see cref="VaultSaveTimeoutMs"/> has passed with no answer. True only for a save the
        /// database confirmed.
        ///
        /// The callback arrives on the SerializedShardDatabase worker thread, not on this one, so the
        /// handoff is a Monitor pulse over two locals and nothing else. Monitor rather than an event
        /// object on purpose: a callback that arrives AFTER the timeout would call Set on a disposed
        /// ManualResetEventSlim and throw on the database thread, whereas a late pulse against a lock
        /// nobody is waiting on is inert.
        ///
        /// A timeout is reported as failure. That is the recoverable direction: the caller destroys a
        /// container it has not registered, and destroying an unregistered, never-populated container
        /// cannot lose an item. Reporting success instead would put an index row in front of a save
        /// that may never land, which is the exact failure this method exists to prevent.
        ///
        /// Only ever called for a new, empty vault container - which is why blocking here is affordable
        /// at all. It happens once per vault, not once per deposit.
        ///
        /// THIS BLOCKS THE WORLD TICK THREAD. GameActionSellItems calls HandleActionSellItem inline on
        /// the world/session tick, AccountVaultStore.Enqueue runs the queued work on the CALLING thread,
        /// and this wait is therefore taken on that same thread: every player being ticked by it is
        /// frozen for however long the shard save takes to confirm. That is why
        /// <see cref="VaultSaveTimeoutMs"/> is a tick-survivable 5 seconds rather than the 30 the
        /// database side alone would justify, and why anything over
        /// <see cref="VaultSaveWarnElapsedMs"/> is logged at ERROR - a stall here is a server-wide
        /// symptom, not a vault one, and it must be visible in the log rather than absorbed.
        /// </summary>
        private bool SaveAndWait(WorldObject worldObject)
        {
            var gate = new object();
            var landed = false;
            var fired = false;

            var started = Stopwatch.StartNew();

            world.SaveBiota(worldObject, ok =>
            {
                lock (gate)
                {
                    landed = ok;
                    fired = true;
                    Monitor.Pulse(gate);
                }
            });

            lock (gate)
            {
                // Already fired if the world source completed the save inline, which is what the test
                // fake does and what makes this path exercisable without a database.
                if (!fired)
                    Monitor.Wait(gate, VaultSaveTimeoutMs);

                var elapsedMs = started.ElapsedMilliseconds;

                if (!fired)
                {
                    log.Error($"[VAULT] account {AccountId}: no save confirmation for 0x{worldObject?.Guid.Full:X8} after {elapsedMs} ms (timeout {VaultSaveTimeoutMs} ms). Treating it as a failed save. This wait ran on the world tick thread.");
                    return false;
                }

                if (elapsedMs > VaultSaveWarnElapsedMs)
                    log.Error($"[VAULT] account {AccountId}: blocked the world tick thread for {elapsedMs} ms waiting on the save of 0x{worldObject?.Guid.Full:X8}. The shard save queue is backed up.");

                return landed;
            }
        }

        #endregion

        #region Withdraw

        public bool TryWithdraw(VaultEntry entry, int amount, Player actor, out List<WorldObject> withdrawn, out string failReason)
        {
            return TryWithdraw(entry, amount, VaultActor.From(actor), out withdrawn, out failReason);
        }

        /// <summary>
        /// Takes items back out. The mirror of the deposit path, with the same "persist before you
        /// remove" discipline.
        ///
        /// THE CALLER OWNS EVERY RETURNED OBJECT AND MUST DISCHARGE IT. The objects come back parented
        /// to nothing: they are in no container, no inventory and no landblock, and no row anywhere
        /// records that the player has them. There are exactly two legal endings for each one, and this
        /// is a correctness contract rather than a style note, because both of the ways of getting it
        /// wrong are silent.
        ///
        /// 1. DELIVER IT: add it to the actor, then call SaveBiotaToDatabase() on it IMMEDIATELY.
        ///    Deliver-and-forget duplicates the item with no caller bug at all. The item's row still
        ///    reads ContainerId = the vault until something saves it, so if the store then sits idle
        ///    for <see cref="IdleEvictionSeconds"/> the manager retires it, the next GetStore rehydrates
        ///    the vault from the shard, and the vault's inventory load hands back the very item the
        ///    player is already carrying - a second live WorldObject on the same guid.
        ///
        /// 2. GIVE IT BACK: an object that cannot be delivered - a full pack, a failed
        ///    TryCreateInInventoryWithNetworking, any error path - MUST go through
        ///    <see cref="TryReturnWithdrawn"/>. It must NEVER be Destroy()ed. Destroying it removes it
        ///    from the vault and from the player at once, unrecoverably, and the audit log will show a
        ///    withdraw that the player will correctly insist never arrived.
        ///
        /// See <see cref="WithdrawItem"/> for why the item is deliberately not force-saved on the way
        /// out, which is what makes rule 1 the caller's job in the first place.
        ///
        /// <paramref name="takeOrder"/> applies to a GROUP row and to nothing else. It defaults to
        /// <see cref="GroupTakeOrder.Front"/>, which is the panel's documented behaviour; only the
        /// market's sale path passes <see cref="GroupTakeOrder.Back"/>, and only to keep the group's
        /// representative in the vault for as long as a listing names it.
        /// </summary>
        internal bool TryWithdraw(VaultEntry entry, int amount, VaultActor actor, out List<WorldObject> withdrawn, out string failReason,
                                  GroupTakeOrder takeOrder = GroupTakeOrder.Front)
        {
            AssertOnMutationQueue(nameof(TryWithdraw));

            Touch();

            withdrawn = new List<WorldObject>();
            failReason = null;

            if (entry == null)
            {
                failReason = GoneMessage;
                return false;
            }

            lock (stateLock)
            {
                if (!CheckReadyLocked(out failReason))
                    return false;
            }

            if (!TryGetAccess(actor, out var access, out failReason))
                return false;

            if (access != VaultAccess.DepositWithdraw)
            {
                // Mirrors PersonalVendor.TryAuthorizeWithdraw's ternary exactly. No access at all and
                // deposit-only access are different situations and must not collapse into one message:
                // a deposit-only grantee told "you do not have permission to use this vault" has no way
                // to tell a revoked grant from a grant that simply does not include withdraw. The
                // vendor is the usual path here, but a same-assembly caller reaching TryWithdraw
                // directly sees this string, so the two sites must agree.
                failReason = access == VaultAccess.None
                    ? "You do not have permission to use this vault."
                    : "You do not have permission to withdraw from this vault.";
                return false;
            }

            // A stored biota is one indivisible object: there is no split path for it, and there must
            // not be one, because splitting a stack that kept its biota is exactly the modification
            // that made it worth keeping. Silently ignoring the amount was the real hazard here - a
            // player asking for 50 of a stored stack of 200 got all 200 while the audit row recorded
            // 200 against a panel that had shown them asking for 50, which is the shape a dupe
            // investigation cannot resolve afterwards. Refuse before dispatching rather than accept a
            // parameter and drop it.
            //
            // A GROUP row is the one exception, and it is not a relaxation of that rule: a group is
            // several whole indivisible biotas presented on one line, so "2 of 5" means two whole
            // members, never a split of one. WithdrawGroup below is all-or-nothing for the members it
            // was asked for.
            if (!entry.IsLedger && !entry.IsGroup && amount > 0 && amount != entry.Count)
            {
                failReason = "A stored item can only be withdrawn whole.";
                return false;
            }

            // DESIGN 5.3 auto-delist. Fires after every refusal above, so a withdraw that is about to be
            // refused never delists, and guarded because a subscriber's throw must not abort a legitimate
            // withdraw.
            var preWithdraw = PreWithdrawHook;

            if (preWithdraw != null)
            {
                try
                {
                    preWithdraw(AccountId, entry.IsLedger ? (uint?)null : entry.Guid.Full, entry.Wcid, amount);
                }
                catch (Exception ex)
                {
                    log.Error($"[VAULT] account {AccountId}: the pre-withdraw hook threw and was ignored: {ex.GetFullMessage()}");
                }
            }

            var handed = entry.IsLedger
                ? WithdrawFromLedger(entry, amount, actor, withdrawn, out failReason)
                : entry.IsGroup
                    ? WithdrawGroup(entry, amount, actor, takeOrder, withdrawn, out failReason)
                    : WithdrawItem(entry, actor, withdrawn, out failReason);

            // Bumped on the FAILURE path too, unlike the deposit above, and deliberately.
            // WithdrawFromLedger debits the ledger before it builds the stacks; if it then cannot build
            // them it refunds, and if <see cref="RefundLedger"/>'s own write is refused the in-memory
            // ledger keeps the DEBITED count while this returns false. Bumping only on success would
            // leave a window showing the pre-debit count with no later mutation to correct it. One
            // wasted rebuild is the entire cost of covering that.
            Interlocked.Increment(ref version);

            return handed;
        }

        private bool WithdrawFromLedger(VaultEntry entry, int amount, VaultActor actor, List<WorldObject> withdrawn, out string failReason)
        {
            failReason = null;

            if (amount < 1)
            {
                failReason = "Choose how many to withdraw.";
                return false;
            }

            // The probe is instantiated BEFORE the debit now, because the audit row needs its name and
            // the audit row has to precede the debit. It doubles as the "can this wcid still be built"
            // check it always was: a wcid dropped from ace_world must refuse before anything moves.
            var probe = world.CreateNewWorldObject(entry.Wcid);

            if (probe == null)
            {
                log.Error($"[VAULT] account {AccountId}: could not instantiate wcid {entry.Wcid} for a withdraw of {amount}; refusing before the ledger moves.");
                failReason = UnavailableMessage;
                return false;
            }

            // Audit BEFORE the debit. The debit is an autocommitted UPDATE; the stacks it pays for are
            // built in memory and are not persisted until PersonalVendor saves them after delivery. A
            // crash in that window leaves the ledger permanently lower with no biota anywhere, and this
            // row is the only record that makes the loss reconstructable. It records the amount ASKED
            // FOR - any shortfall is corrected by a Return row beside the refund below, so the two
            // balance.
            WriteLog(AccountVaultAction.Withdraw, actor, entry.Wcid, null, probe.Name, amount);

            // The database both applies and adjudicates: REFUSED means the ledger DID NOT MOVE, and an
            // over-withdraw is refused rather than clamped. There is no partial application to undo.
            var debit = backend.TryAdjustAccountVaultStack(AccountId, entry.Wcid, -amount, out var newCount);

            if (debit == AccountVaultStackAdjustResult.Refused)
            {
                failReason = GoneMessage;

                // The Withdraw row above recorded an ask that the database then refused, so it has to be
                // balanced or the audit trail shows units leaving a vault they never left. The debit is
                // adjudicated synchronously here - there is no crash window between the two rows to
                // worry about, unlike the window between the debit and the delivery save that the
                // early Withdraw row exists for in the first place.
                //
                // Fix round 2, F1: this balancing row is written ONLY for a provable refusal. It used to
                // be written for a database failure too, and that is the bug: a debit that had committed
                // and then failed its read-back produced a Withdraw row, a Return row that cancels it,
                // and no items - a real loss recorded in the audit trail as a non-event.
                WriteLog(AccountVaultAction.Return, actor, entry.Wcid, null, probe.Name, amount);

                // The probe is built before the debit now, so this path is the one place that creates an
                // object and then does not deliver it. Today's ordering never got here holding one.
                world.DestroyItem(probe);

                return false;
            }

            if (debit == AccountVaultStackAdjustResult.Failed)
            {
                // See DepositToLedger's asymmetry remark for why this proceeds where the deposit path
                // refuses. In one line: the units may already be gone from the ledger and the player has
                // nothing, so the recoverable direction here is to hand them the items and leave a
                // marker, not to write a Return row that erases the debit from the audit trail.
                log.Error($"[VAULT] LEDGER STATE UNKNOWN: account {AccountId}, wcid {entry.Wcid}, delta -{amount}. A withdraw debit neither committed nor provably failed. The items ARE being handed over and NO balancing Return row is written, because writing one over a debit that did land would erase a real loss. If the debit did NOT land, this account has been paid twice for those units - reconcile against account_vault_log.");
            }

            if (debit == AccountVaultStackAdjustResult.Applied)
                ApplyLedgerCount(entry.Wcid, newCount);
            else
                InvalidateLedger($"a withdraw debit of {amount} x wcid {entry.Wcid} with an unknown resulting count");

            var created = new List<WorldObject>();

            created.Add(probe);

            // The per-stack cap is read off a created object rather than hardcoded, because
            // MaxStackSize is world-database data. CalcPayoutStackSizes is the split retail's payout
            // path already uses (Player_Commerce.cs:552); there must not be a second implementation.
            var maxStackSize = probe.MaxStackSize ?? 1;

            if (maxStackSize < 1)
                maxStackSize = 1;

            var sizes = Player.CalcPayoutStackSizes(amount, maxStackSize);

            var delivered = 0;

            for (var i = 0; i < sizes.Count; i++)
            {
                var stack = i == 0 ? probe : world.CreateNewWorldObject(entry.Wcid);

                if (stack == null)
                {
                    log.Error($"[VAULT] account {AccountId}: ran out of objects part way through a withdraw of {amount} x wcid {entry.Wcid}; refunding the undelivered remainder.");

                    foreach (var orphan in created)
                        world.DestroyItem(orphan);

                    RefundLedger(entry.Wcid, amount);
                    WriteLog(AccountVaultAction.Return, actor, entry.Wcid, null, probe.Name, amount);

                    failReason = UnavailableMessage;
                    return false;
                }

                if (i > 0)
                    created.Add(stack);

                stack.SetStackSize(sizes[i]);

                // Read the placed size back rather than assuming it: SetStackSize is a no-op on
                // anything that is not a Stackable, so a wcid retyped away from Stackable would
                // otherwise be reported as a full stack while one unit was handed over.
                delivered += stack.StackSize ?? 1;
            }

            if (delivered != amount)
            {
                log.Error($"[VAULT] account {AccountId}: withdraw of wcid {entry.Wcid} asked for {amount} units and could only build {delivered}; refunding the difference.");
                RefundLedger(entry.Wcid, amount - delivered);

                // The Withdraw row above recorded the full ask. Without this the audit log would show
                // more leaving the vault than ever did, which is exactly the unresolvable shape the
                // audit trail exists to prevent.
                WriteLog(AccountVaultAction.Return, actor, entry.Wcid, null, probe.Name, amount - delivered);
            }

            withdrawn.AddRange(created);

            // Gated on Applied as well as on the count (fix round 2, F1): newCount is 0 for every other
            // outcome, and 0 there is "unknown", not "the row is empty".
            if (debit == AccountVaultStackAdjustResult.Applied && newCount <= 0)
            {
                // Opportunistic tidy-up of the zero row. It runs HERE, on the store's mutation queue,
                // and that placement is load-bearing rather than incidental: TryAdjustAccountVaultStack
                // is an ensure-row statement followed by a guarded UPDATE, so a concurrent delete for
                // this account landing between the two would make a legitimate deposit return false.
                // Same queue as every other mutation excludes that entirely.
                backend.DeleteEmptyAccountVaultStacks(AccountId);
            }

            return true;
        }

        /// <summary>
        /// Takes the first <paramref name="amount"/> members of a group row out of their vaults.
        ///
        /// ALL OR NOTHING, and that is a correctness requirement rather than tidiness. The vendor's
        /// basket loop enrolls withdrawn objects in its unwind list only AFTER this returns true (or
        /// after it throws, via the out parameter); a plain `false` return with objects already removed
        /// would leak every one of them - gone from the in-memory vault, gone from the panel, held by
        /// nobody, and resurrected as a live duplicate the next time the store rehydrates from rows
        /// that still point at the vault. So every member is RESOLVED against the live vaults first,
        /// under one stateLock acquisition, and only then are any of them removed; if a removal still
        /// fails after that, whatever was taken is put straight back before returning.
        ///
        /// Members are taken from the front of the list by default, so repeated withdrawals from the
        /// same panel are reproducible (VaultEntry.Members documents that ordering as part of the
        /// contract). <see cref="GroupTakeOrder.Back"/> takes the LAST <paramref name="amount"/>
        /// members instead, in list order, and is asked for by the market sale alone so a listing
        /// pinned to Members[0] never loses its anchor while it still has members to sell.
        /// </summary>
        private bool WithdrawGroup(VaultEntry entry, int amount, VaultActor actor, GroupTakeOrder takeOrder,
                                   List<WorldObject> withdrawn, out string failReason)
        {
            failReason = null;

            if (amount < 1)
            {
                failReason = "Choose how many to withdraw.";
                return false;
            }

            // Refused, not clamped, matching the ledger's own over-withdraw behaviour: a player who
            // asked for more than the row holds gets told so rather than quietly handed fewer.
            if (amount > entry.Count)
            {
                failReason = $"Your vault holds only {entry.Count} of those.";
                return false;
            }

            var taken = new List<WorldObject>();
            var touched = new List<Container>();

            lock (stateLock)
            {
                // Re-resolved against the live vaults rather than trusted off the entry, exactly as
                // WithdrawItem does, so a second window that already took one of these members loses
                // cleanly instead of transacting twice.
                var located = new List<(Container Vault, ObjectGuid Guid)>();

                // Front takes 0..amount-1; Back takes the last `amount`, still in list order. The
                // over-withdraw refusal above bounds amount to entry.Count, and entry.Count IS
                // Members.Count for a group, so the Back offset cannot go negative.
                var offset = takeOrder == GroupTakeOrder.Back ? entry.Members.Count - amount : 0;

                for (var i = 0; i < amount; i++)
                {
                    var member = entry.Members[offset + i];

                    if (member == null)
                    {
                        failReason = GoneMessage;
                        return false;
                    }

                    Container owner = null;

                    foreach (var candidate in vaults)
                    {
                        if (candidate.Inventory.ContainsKey(member.Guid))
                        {
                            owner = candidate;
                            break;
                        }
                    }

                    if (owner == null)
                    {
                        failReason = GoneMessage;
                        return false;
                    }

                    located.Add((owner, member.Guid));
                }

                try
                {
                    foreach (var (vault, guid) in located)
                    {
                        if (!vault.TryRemoveFromInventory(guid, out var item))
                        {
                            // Unreachable in principle - every guid was found in that same container
                            // under this same lock a few lines ago - but a half-emptied group is the one
                            // outcome this method exists to make impossible, so it is unwound rather
                            // than trusted.
                            log.Error($"[VAULT] account {AccountId}: a group withdraw could not remove 0x{guid.Full:X8} from vault 0x{vault.Guid.Full:X8} after locating it; putting back the {taken.Count} member(s) already taken.");

                            for (var i = 0; i < taken.Count; i++)
                            {
                                if (!located[i].Vault.TryAddToInventory(taken[i]))
                                    log.Error($"[VAULT] ORPHAN: account {AccountId}: could not put 0x{taken[i].Guid.Full:X8} ({taken[i].Name}) back into vault 0x{located[i].Vault.Guid.Full:X8} while unwinding a group withdraw. It has NOT been destroyed and needs manual recovery.");
                            }

                            failReason = GoneMessage;
                            return false;
                        }

                        taken.Add(item);

                        if (!touched.Contains(vault))
                            touched.Add(vault);
                    }
                }
                finally
                {
                    // EVERY exit from the mutation phase drops the memo: the success path, the unwind
                    // path above, and an exception out of either. Inside the lock, because the version
                    // bump that would otherwise signal this happens well after the lock is released -
                    // see groupingMemo's remarks.
                    InvalidateGroupingLocked();
                }
            }

            // Same discipline as WithdrawItem: the CONTAINER is saved, the items deliberately are not.
            // See TryWithdraw's contract for why leaving each item's row pointing at the vault is the
            // recoverable direction until the caller has placed it somewhere.
            foreach (var vault in touched)
                world.SaveBiota(vault);

            foreach (var item in taken)
            {
                withdrawn.Add(item);

                WriteLog(AccountVaultAction.Withdraw, actor, item.WeenieClassId, item.Guid.Full, item.Name, item.StackSize ?? 1);
            }

            return true;
        }

        private bool WithdrawItem(VaultEntry entry, VaultActor actor, List<WorldObject> withdrawn, out string failReason)
        {
            failReason = null;

            Container vault = null;
            WorldObject item = null;

            lock (stateLock)
            {
                foreach (var candidate in vaults)
                {
                    if (candidate.Inventory.ContainsKey(entry.Guid))
                    {
                        vault = candidate;
                        break;
                    }
                }

                // Re-resolved against the live vaults rather than trusted off the entry, so a second
                // window that already took this item loses cleanly instead of transacting twice.
                if (vault == null || !vault.TryRemoveFromInventory(entry.Guid, out item))
                {
                    failReason = GoneMessage;
                    return false;
                }

                // Inside the lock, beside the removal. The version bump is several statements and one
                // database round-trip away - see groupingMemo's remarks.
                InvalidateGroupingLocked();
            }

            // forceSave is deliberately NOT used on the item. TryRemoveFromInventory has already
            // cleared its ContainerId in memory; persisting that before the caller has put the item
            // anywhere would leave a row with no container, no wielder and no location - exactly the
            // orphan the purge is entitled to delete (R1). Leaving the row pointing at the vault means
            // a crash in this window returns the item to the vault on restart, which is the
            // recoverable direction. The container save copies Storage.OnRemoveItem (Storage.cs:93-101).
            world.SaveBiota(vault);

            withdrawn.Add(item);

            WriteLog(AccountVaultAction.Withdraw, actor, item.WeenieClassId, item.Guid.Full, item.Name, item.StackSize ?? 1);

            return true;
        }

        /// <summary>
        /// Puts back an object that <see cref="TryWithdraw"/> handed out and the caller could not
        /// deliver. This is the second legal ending named in TryWithdraw's contract, and it exists so
        /// that Destroy() never has to be the caller's answer to a full pack.
        ///
        /// Deliberately NOT a deposit, and it skips three things TryDeposit does, each for the same
        /// reason: the item was already in this account's vault moments ago.
        ///
        /// - No cap check. The item was counted against the cap while it sat in the vault, and it was
        ///   never handed to anyone, so refusing it here would strand it in the caller's hands with
        ///   nowhere legal to put it. That is the one outcome this method exists to make impossible.
        ///   This is sound ONLY because ledger-derived objects no longer arrive here - they go to
        ///   TryReturnWithdrawnToLedger instead. Do not generalize the exemption back out.
        /// - No access check. The actor's right to withdraw was decided when the withdraw ran; this is
        ///   the undo of that decision, not a new one, and a grant revoked in the intervening
        ///   microseconds must not turn a failed delivery into a lost item.
        /// - No collapse test. The item's biota is the same one the vault was holding, and it was
        ///   stored as a biota precisely because it did not collapse.
        ///
        /// Runs on the mutation queue like every other mutation, because it takes a free slot and adds
        /// to a container - the exact biota path the queue protects.
        ///
        /// It DOES write an audit row, <see cref="AccountVaultAction.Return"/>, and that row is not
        /// bookkeeping. Every caller reaches this method through <see cref="TryWithdraw"/>, which has
        /// already written a Withdraw row; leaving the return unrecorded produces exactly the shape the
        /// withdraw contract exists to prevent, one step later - a log saying an item left the vault
        /// while the item is in fact sitting back inside it, unresolvable by any investigation reading
        /// the log alone. A log line is not an audit trail: it is not queryable beside the other rows.
        ///
        /// The <paramref name="actor"/> is always in hand at the call site, because TryWithdraw took
        /// one to get here. Nothing has to invent it.
        /// </summary>
        internal bool TryReturnWithdrawn(WorldObject item, VaultActor actor)
        {
            AssertOnMutationQueue(nameof(TryReturnWithdrawn));

            Touch();

            if (item == null)
                return false;

            lock (stateLock)
            {
                // The store cannot pick a free slot it cannot see (R4), so a not-ready store refuses
                // rather than guessing. The caller keeps the object and must not destroy it; a return
                // it cannot complete is not licence to break the other half of the contract.
                if (!CheckReadyLocked(out _))
                {
                    log.Error($"[VAULT] account {AccountId}: could not return 0x{item.Guid.Full:X8} because the store is not ready. The CALLER still holds it and must not destroy it.");
                    return false;
                }
            }

            if (item.ContainerId != null || item.WielderId != null)
            {
                log.Error($"[VAULT] account {AccountId}: refused a return of 0x{item.Guid.Full:X8} because it is still parented (container {item.ContainerId}, wielder {item.WielderId}). A delivered item is not a returnable one.");
                return false;
            }

            var vault = FindOrCreateVault();

            if (vault == null)
            {
                log.Error($"[VAULT] account {AccountId}: could not return 0x{item.Guid.Full:X8} to any vault. The CALLER still holds it and must not destroy it.");
                return false;
            }

            lock (stateLock)
            {
                if (!vault.TryAddToInventory(item))
                {
                    log.Error($"[VAULT] account {AccountId}: TryAddToInventory refused returned item 0x{item.Guid.Full:X8} on vault 0x{vault.Guid.Full:X8} ({vault.Inventory.Count} of {vault.ItemCapacity} slots used). The CALLER still holds it and must not destroy it.");
                    return false;
                }

                // Inside the lock, beside the mutation - the version bump is two database round-trips
                // away at the end of this method. See groupingMemo's remarks.
                InvalidateGroupingLocked();
            }

            // Same save discipline as a deposit, and for the same reason: until the item's row points
            // at the vault again it points at nothing, and the orphan purge would be entitled to it
            // (R1). The container save copies Storage.OnAddItem (Storage.cs:80-91).
            world.SaveBiota(item);
            world.SaveBiota(vault);

            // AFTER the return has actually succeeded, like every other row this class writes: the
            // audit records what happened, never what was about to be attempted. Every refusal path
            // above returns without reaching this, so a refused return writes nothing - a Return row
            // for an item still stuck in the caller's hands would be worse than no row at all.
            WriteLog(AccountVaultAction.Return, actor, item.WeenieClassId, item.Guid.Full, item.Name, item.StackSize ?? 1);

            log.Warn($"[VAULT] account {AccountId}: item 0x{item.Guid.Full:X8} ({item.Name}) was withdrawn and could not be delivered, so it was returned to vault 0x{vault.Guid.Full:X8}. Audited as a Return against the Withdraw row above it.");

            // The item is back in a vault container, so it is a row again.
            Interlocked.Increment(ref version);

            return true;
        }

        /// <summary>
        /// The undo of a LEDGER withdrawal: credits the units back to the ledger row and destroys the
        /// object that was built to carry them.
        ///
        /// This exists because TryReturnWithdrawn is the wrong undo for a ledger-sourced object.
        /// TryReturnWithdrawn skips the capacity check, correctly, on the reasoning that a stored biota
        /// was already counted against the cap while it sat in the vault. A ledger-derived object was
        /// NOT counted: one ledger row is one entry against the cap no matter how many units it holds
        /// (DESIGN 7.3), so filing one back as a stored biota converts one entry into as many entries
        /// as there were units, past the cap and with no bound on the vault containers created.
        ///
        /// Credit BEFORE destroy, matching the deposit path's ordering rationale: a crash between the
        /// two loses a rebuildable object, while the reverse loses the units themselves.
        /// </summary>
        internal bool TryReturnWithdrawnToLedger(WorldObject item, uint wcid, long units, VaultActor actor)
        {
            AssertOnMutationQueue(nameof(TryReturnWithdrawnToLedger));

            Touch();

            if (item == null || units <= 0)
                return false;

            if (item.ContainerId != null || item.WielderId != null)
            {
                log.Error($"[VAULT] account {AccountId}: refused a ledger return of 0x{item.Guid.Full:X8} because it is still parented (container {item.ContainerId}, wielder {item.WielderId}). A delivered item is not a returnable one.");
                return false;
            }

            var credit = backend.TryAdjustAccountVaultStack(AccountId, wcid, units, out var newCount);

            if (credit == AccountVaultStackAdjustResult.Refused)
            {
                log.Error($"[VAULT] account {AccountId}: could not credit {units} units of wcid {wcid} back to the ledger. The CALLER still holds 0x{item.Guid.Full:X8} and must not destroy it.");
                return false;
            }

            // Fix round 2, F1: a return proceeds on Failed as well as on AppliedCountUnknown, which is
            // the DEPOSIT decision inverted - and deliberately, because the retry hazard this method
            // already documents below is what makes the deposit's answer wrong here. A deposit that
            // refuses hands the item back to a player who keeps it; a RETURN that refuses hands it back
            // to PersonalVendor's unwind, which retries the same object against this same method. If
            // the first credit did land, that retry credits the units a second time, and nothing about
            // a ledger-returned object is parented, so the guard above cannot refuse it. One possibly
            // orphaned object beats an unbounded double-credit, and the marker makes it findable.
            if (credit == AccountVaultStackAdjustResult.Failed)
                log.Error($"[VAULT] LEDGER STATE UNKNOWN: account {AccountId}, wcid {wcid}, delta +{units}. A ledger RETURN credit neither committed nor provably failed. It is being treated as applied and 0x{item.Guid.Full:X8} will be destroyed, because retrying the return would double the credit if it did land. If it did not, those units are lost - reconcile against account_vault_log.");

            if (credit == AccountVaultStackAdjustResult.Applied)
                ApplyLedgerCount(wcid, newCount);
            else
                InvalidateLedger($"a ledger return of {units} x wcid {wcid} with an unknown resulting count");

            // The credit above is the point of no return, and everything below it is bookkeeping and
            // teardown. If either throws, the exception unwinds through Drain, which logs and does not
            // rethrow (see Drain's catch), so Enqueue still reports success while `returnedOk` in the
            // caller is left false - the caller then reads that as a failed return and retries the SAME
            // item. Nothing about a ledger-returned item is parented, so the guard above would not refuse
            // that retry, and TryAdjustAccountVaultStack would credit the same units twice.
            //
            // TryReturnWithdrawn does not need this: TryAddToInventory sets item.ContainerId before its
            // own throw-prone steps, so a retry's parented guard refuses. This path has no equivalent
            // mark, so the success has to be made authoritative here instead.
            try
            {
                WriteLog(AccountVaultAction.Return, actor, wcid, null, item.Name, units);

                world.DestroyItem(item);
            }
            catch (Exception ex)
            {
                log.Error($"[VAULT] account {AccountId}: credited {units} units of wcid {wcid} back to the ledger, but the audit row or the teardown of 0x{item.Guid.Full:X8} threw: {ex.GetFullMessage()}. The credit stands and the return is NOT retried - retrying it would double it. The object is now unparented and unreferenced.");
            }

            // After the catch as well as after the try, because the credit above is the point of no
            // return: the ledger row has moved whether or not the teardown threw.
            Interlocked.Increment(ref version);

            return true;
        }

        private void ApplyLedgerCount(uint wcid, long newCount)
        {
            lock (stateLock)
            {
                if (newCount > 0)
                    ledger[wcid] = newCount;
                else
                    ledger.Remove(wcid);
            }
        }

        private void RefundLedger(uint wcid, long units)
        {
            if (units <= 0)
                return;

            var refund = backend.TryAdjustAccountVaultStack(AccountId, wcid, units, out var restored);

            if (refund == AccountVaultStackAdjustResult.Applied)
            {
                ApplyLedgerCount(wcid, restored);
                return;
            }

            // Fix round 2, F1: an unknown outcome is NOT a failed refund. The units may well be back;
            // what is not known is the resulting count, so the ledger is re-read rather than reported
            // as lost. Reporting a loss that did not happen sends an operator hunting for units that
            // are sitting in the row all along.
            if (refund != AccountVaultStackAdjustResult.Refused)
            {
                log.Error($"[VAULT] LEDGER STATE UNKNOWN: account {AccountId}, wcid {wcid}, delta +{units}. A refund credit's outcome could not be confirmed; the ledger is being re-read rather than the units reported lost.");

                InvalidateLedger($"a refund of {units} x wcid {wcid} with an unknown resulting count");
                return;
            }

            // Nothing else can be done from here, and the audit row plus this line are what make the
            // loss reconstructible.
            log.Error($"[VAULT] account {AccountId}: COULD NOT REFUND {units} x wcid {wcid} to the ledger. Those units are gone from the store and were not handed to the player.");
        }

        #endregion

        #region Barrel (Docs/Market/GIVEAWAY-BULK-BARREL-DESIGN.md section 6)

        /// <summary>The refusal when the barrel container has no free slot left.</summary>
        public const string BarrelFullMessage = "The barrel is full. An administrator has to empty it before anything else can go in.";

        /// <summary>
        /// Destroys part of the store by feeding it to the account's barrel. A SOFT delete: a stored
        /// item moves biota-intact into the barrel container and an administrator can put it back
        /// through <see cref="TryRestoreFromBarrel"/> until retention purges it.
        ///
        /// Returns false with a player-facing <paramref name="failReason"/> on every refusal, and
        /// nothing has moved when it does.
        ///
        /// THE ROW ORDER IS OPPOSITE ON THE TWO PATHS, and that is deliberate rather than
        /// inconsistent. One principle decides both: never leave behind a row that could, on its own,
        /// create or destroy value.
        ///
        ///   - STORED ITEM: the account_vault_barrel row is written BEFORE the item moves. A stray row
        ///     whose item never moved is inert - the retention sweep destroys only what it finds
        ///     inside the barrel container, and a restore likewise moves only what is really there -
        ///     so the worst case is a row an admin can see and ignore. The opposite order would leave
        ///     an item sitting in the barrel that no row names, invisible to the restore command.
        ///   - LEDGER: the decrement happens FIRST and the row is written after. A stray row here
        ///     would be a DUPE, because a restore against it would credit units that were never
        ///     debited. Only <see cref="AccountVaultStackAdjustResult.Refused"/> proves the ledger did
        ///     not move; see the branch below for what each of the other three obliges.
        ///
        /// Concurrency discipline is TryDeposit's, copied rather than re-invented: every mutation of a
        /// container's inventory happens inside `lock (stateLock)` with
        /// <see cref="InvalidateGroupingLocked"/> called BESIDE the mutation, and
        /// <see cref="Interlocked.Increment(ref long)"/> runs the moment the item has physically
        /// moved rather than on the success return, because everything after that point can throw.
        /// </summary>
        internal bool TryBarrel(VaultEntry entry, int amount, VaultActor actor, out string failReason)
        {
            AssertOnMutationQueue(nameof(TryBarrel));

            Touch();

            failReason = null;

            if (entry == null)
            {
                failReason = GoneMessage;
                return false;
            }

            if (amount < 1)
            {
                failReason = "Choose how many to destroy.";
                return false;
            }

            lock (stateLock)
            {
                if (!CheckReadyLocked(out failReason))
                    return false;
            }

            if (!TryGetAccess(actor, out var access, out failReason))
                return false;

            // Withdraw-level access, not deposit-level. Barreling is strictly more destructive than
            // withdrawing, and a deposit-only grantee may not take an item out of the owner's vault,
            // so they certainly may not destroy one. Anyone who CAN withdraw could withdraw and
            // destroy the item by hand anyway, so this is the same authority rather than a new one.
            if (access != VaultAccess.DepositWithdraw)
            {
                failReason = access == VaultAccess.None
                    ? "You do not have permission to use this vault."
                    : "You do not have permission to withdraw from this vault.";
                return false;
            }

            if (amount > entry.Count)
            {
                failReason = $"Your vault holds only {entry.Count} of those.";
                return false;
            }

            // A stored biota is indivisible, exactly as it is on the withdraw path: there is no split
            // of a stored stack, so an amount that is not the whole thing is refused rather than
            // silently rounded up to it. A GROUP row is the one exception and is not a relaxation -
            // its Count is a number of whole MEMBERS, so "2 of 5" means two whole biotas.
            if (!entry.IsLedger && !entry.IsGroup && amount != entry.Count)
            {
                failReason = "A stored item can only be destroyed whole.";
                return false;
            }

            if (IsListed(entry))
            {
                failReason = ItemListedMessage;
                return false;
            }

            return entry.IsLedger
                ? BarrelLedger(entry, amount, actor, out failReason)
                : BarrelStoredItems(entry, amount, actor, out failReason);
        }

        /// <summary>
        /// Whether this entry currently carries an ACTIVE market listing, through
        /// <see cref="IsListedHook"/>. A missing hook, and a hook that throws, both answer "not
        /// listed" - see that field for why that default is safe here and where the authoritative
        /// check lives.
        /// </summary>
        private bool IsListed(VaultEntry entry)
        {
            var hook = IsListedHook;

            if (hook == null)
                return false;

            try
            {
                return hook(AccountId, entry.IsLedger ? (uint?)null : entry.Guid.Full, entry.Wcid);
            }
            catch (Exception ex)
            {
                log.Error($"[VAULT] account {AccountId}: the market listing hook threw and was read as 'not listed': {ex.GetFullMessage()}");
                return false;
            }
        }

        /// <summary>
        /// Barrels whole stored biotas: the entry itself for an ordinary stored item, or the first
        /// <paramref name="amount"/> members of a group row.
        ///
        /// ALL OR NOTHING on the move, copied from <see cref="WithdrawGroup"/> and for the same
        /// reason: every target is RESOLVED against the live vaults first, under one stateLock
        /// acquisition, and only then is anything moved; a failure part way through puts back whatever
        /// was taken before returning.
        /// </summary>
        private bool BarrelStoredItems(VaultEntry entry, int amount, VaultActor actor, out string failReason)
        {
            failReason = null;

            var targets = new List<WorldObject>();

            if (entry.IsGroup)
            {
                // Front, matching VaultEntry.Members' documented ordering: the same panel action twice
                // does the same thing.
                for (var i = 0; i < amount && i < entry.Members.Count; i++)
                    targets.Add(entry.Members[i]);
            }
            else
            {
                targets.Add(entry.WorldObject);
            }

            if (targets.Count == 0 || targets.Any(t => t == null))
            {
                failReason = GoneMessage;
                return false;
            }

            var destination = FindOrCreateBarrel();

            if (destination == null)
            {
                failReason = BarrelUnavailableMessage;
                return false;
            }

            // R4 applied to the barrel: its inventory load is async like any container's, and an
            // unloaded container reports Inventory.Count 0, which is indistinguishable from empty. A
            // barreling into one would look like it fit and could overflow the real capacity.
            if (!destination.InventoryLoaded)
            {
                failReason = BarrelUnavailableMessage;
                return false;
            }

            // Snapshotted BEFORE anything moves, so the rows still name what was thrown away after the
            // items themselves are destroyed.
            var snapshots = targets
                .Select(t => (Guid: t.Guid.Full, Wcid: t.WeenieClassId, Name: t.Name, Count: (long)(t.StackSize ?? 1)))
                .ToList();

            var located = new List<(Container Vault, ObjectGuid Guid)>();
            var moved = new List<WorldObject>();
            var touched = new List<Container>();

            lock (stateLock)
            {
                if (destination.Inventory.Count + targets.Count > (destination.ItemCapacity ?? 0))
                {
                    failReason = BarrelFullMessage;
                    return false;
                }

                // Re-resolved against the live vaults rather than trusted off the entry, so a second
                // window that already took one of these loses cleanly instead of transacting twice.
                foreach (var target in targets)
                {
                    Container owner = null;

                    foreach (var candidate in vaults)
                    {
                        if (candidate.Inventory.ContainsKey(target.Guid))
                        {
                            owner = candidate;
                            break;
                        }
                    }

                    if (owner == null)
                    {
                        failReason = GoneMessage;
                        return false;
                    }

                    located.Add((owner, target.Guid));
                }
            }

            // The audit rows go in BEFORE the move - see TryBarrel's remarks for why a stray row is
            // inert on this path and an unnamed item in the barrel is not. Written outside stateLock,
            // because they are database round-trips and this store never holds its lock across one.
            var rows = new List<AccountVaultBarrel>();

            foreach (var snapshot in snapshots)
            {
                var row = new AccountVaultBarrel
                {
                    AccountId = AccountId,
                    Wcid = snapshot.Wcid,
                    ItemGuid = snapshot.Guid,
                    Count = snapshot.Count,
                    ItemName = string.IsNullOrEmpty(snapshot.Name) ? "<unknown>" : snapshot.Name,
                    BarreledAt = DateTime.UtcNow,
                    ActorCharacterGuid = actor.CharacterGuid,
                    ActorCharacterName = string.IsNullOrEmpty(actor.Name) ? "<unknown>" : actor.Name,
                };

                if (!backend.AddAccountVaultBarrel(row))
                {
                    log.Error($"[VAULT] account {AccountId}: could not record the barreling of 0x{snapshot.Guid:X8} ({snapshot.Name}); refusing before anything moves, because an item in the barrel that no row names is invisible to /vaultrestore.");
                    failReason = BarrelUnavailableMessage;
                    return false;
                }

                rows.Add(row);
            }

            lock (stateLock)
            {
                try
                {
                    foreach (var (vault, guid) in located)
                    {
                        if (!vault.TryRemoveFromInventory(guid, out var item) || !destination.TryAddToInventory(item))
                        {
                            log.Error($"[VAULT] account {AccountId}: a barreling could not move 0x{guid.Full:X8} out of vault 0x{vault.Guid.Full:X8} after locating it; putting back the {moved.Count} item(s) already moved. account_vault_barrel row(s) {string.Join(", ", rows.Select(r => r.Id))} were already written and are INERT - retention destroys only what it finds inside the barrel, and a restore likewise moves only what is really there.");

                            for (var i = 0; i < moved.Count; i++)
                            {
                                if (!destination.TryRemoveFromInventory(moved[i].Guid, out _) || !located[i].Vault.TryAddToInventory(moved[i]))
                                    log.Error($"[VAULT] ORPHAN: account {AccountId}: could not put 0x{moved[i].Guid.Full:X8} ({moved[i].Name}) back into vault 0x{located[i].Vault.Guid.Full:X8} while unwinding a barreling. It has NOT been destroyed and needs manual recovery.");
                            }

                            failReason = GoneMessage;
                            return false;
                        }

                        moved.Add(item);

                        if (!touched.Contains(vault))
                            touched.Add(vault);
                    }
                }
                finally
                {
                    // EVERY exit from the mutation phase drops the memo, the unwind included, and it
                    // happens INSIDE the lock: the version bump that would otherwise signal it runs
                    // well after the lock is released. See groupingMemo's remarks.
                    InvalidateGroupingLocked();
                }
            }

            // Bumped HERE, the moment the items are physically out of the vaults, not on the success
            // return: everything below can throw, and a throw unwinds past any bump placed after it.
            Interlocked.Increment(ref version);

            // Each item's row now points at the barrel, and nothing else will ever re-save it - the
            // barrel is filtered out of landblocks, so Landblock.SaveDB never sees it. A dropped write
            // here is an item that comes back in its OLD vault after a restart, which is the
            // recoverable direction but still has to be retried and said out loud.
            foreach (var item in moved)
            {
                var savedGuid = item.Guid.Full;
                var savedWcid = item.WeenieClassId;

                world.SaveBiota(item, saved =>
                {
                    if (saved)
                        return;

                    log.Error($"[VAULT] BARREL SAVE FAILED for account {AccountId}: 0x{savedGuid:X8} (wcid {savedWcid}) is in the barrel in memory but its biota did not persist. Queued for retry.");

                    lock (stateLock)
                    {
                        if (!pendingDepositSaves.ContainsKey(item))
                            pendingDepositSaves[item] = 0;
                    }
                });
            }

            foreach (var vault in touched)
                world.SaveBiota(vault);

            world.SaveBiota(destination);

            foreach (var snapshot in snapshots)
                WriteLog(AccountVaultAction.Barrel, actor, snapshot.Wcid, snapshot.Guid, snapshot.Name, snapshot.Count);

            return true;
        }

        /// <summary>
        /// Barrels units off a ledger row. There is no biota by definition, so nothing moves anywhere:
        /// the units are debited and the row records wcid plus count with a null item guid.
        ///
        /// The DEBIT COMES FIRST here, the opposite of the stored-item path, because a row written
        /// over a debit that never happened is a dupe waiting for the first restore. See TryBarrel's
        /// remarks for the principle both orders come from.
        /// </summary>
        private bool BarrelLedger(VaultEntry entry, int amount, VaultActor actor, out string failReason)
        {
            failReason = null;

            // A probe purely for the NAME, built and destroyed the way the ledger withdraw path builds
            // one: the row has to name what was thrown away, and a ledger row has no object to read a
            // name from. It doubles as the "can this wcid still be built" check - but unlike the
            // withdraw path, a wcid dropped from ace_world must NOT refuse here, because the player is
            // destroying the units and refusing would strand them in a vault forever.
            var probe = world.CreateNewWorldObject(entry.Wcid);
            var name = probe?.Name ?? $"Item {entry.Wcid}";

            if (probe != null)
                world.DestroyItem(probe);

            var debit = backend.TryAdjustAccountVaultStack(AccountId, entry.Wcid, -amount, out var newCount);

            if (debit == AccountVaultStackAdjustResult.Refused)
            {
                // The ONE outcome that proves the ledger did not move. Nothing has happened and the
                // player keeps the units.
                log.Error($"[VAULT] account {AccountId}: the ledger refused a barreling of {amount} x wcid {entry.Wcid}. Nothing was destroyed.");
                failReason = UnavailableMessage;
                return false;
            }

            if (debit == AccountVaultStackAdjustResult.Failed)
            {
                // Nobody knows whether the debit landed. The recoverable direction is to RECORD the
                // barreling anyway and say so loudly: if the debit did land, the row is what makes the
                // units restorable, and if it did not, the row is inert until someone reconciles it.
                // Refusing instead would lose the units silently in the case that they really went.
                log.Error($"[VAULT] LEDGER STATE UNKNOWN: account {AccountId}, wcid {entry.Wcid}, delta -{amount}. A barrel debit neither committed nor provably failed. The account_vault_barrel row IS being written, because it is the only thing that makes the units restorable if the debit did land - reconcile it against account_vault_log before restoring.");

                InvalidateLedger($"a barrel debit of {amount} x wcid {entry.Wcid} with an unknown outcome");
            }
            else if (debit == AccountVaultStackAdjustResult.AppliedCountUnknown)
            {
                // The units ARE gone from the ledger; only the resulting count could not be read back.
                InvalidateLedger($"a barrel debit of {amount} x wcid {entry.Wcid} whose count could not be read back");
            }
            else
            {
                ApplyLedgerCount(entry.Wcid, newCount);
            }

            // Bumped the moment the units are committed to being gone, not on the success return.
            Interlocked.Increment(ref version);

            var row = new AccountVaultBarrel
            {
                AccountId = AccountId,
                Wcid = entry.Wcid,
                ItemGuid = null,
                Count = amount,
                ItemName = name,
                BarreledAt = DateTime.UtcNow,
                ActorCharacterGuid = actor.CharacterGuid,
                ActorCharacterName = string.IsNullOrEmpty(actor.Name) ? "<unknown>" : actor.Name,
            };

            if (!backend.AddAccountVaultBarrel(row))
            {
                // The units are already gone and cannot be put back without risking a dupe on top of a
                // debit whose state may itself be unknown. account_vault_log below is then the ONLY
                // record of the barreling, which is exactly what it is for.
                log.Error($"[VAULT] account {AccountId}: BARREL ROW MISSING for {amount} x wcid {entry.Wcid} ({name}). The units are debited and are NOT restorable through /vaultrestore; account_vault_log is the only record.");
            }

            if (debit == AccountVaultStackAdjustResult.Applied && newCount <= 0)
            {
                // Same opportunistic tidy-up as the ledger withdraw, and on the same queue for the same
                // reason: TryAdjustAccountVaultStack is an ensure-row plus a guarded UPDATE, so a
                // concurrent delete for this account landing between the two would make a legitimate
                // deposit return false.
                backend.DeleteEmptyAccountVaultStacks(AccountId);
            }

            WriteLog(AccountVaultAction.Barrel, actor, entry.Wcid, null, name, amount);

            return true;
        }

        /// <summary>The refusal for a row that has already been given back. A restore is not repeatable.</summary>
        public const string AlreadyRestoredMessage = "That has already been restored.";

        /// <summary>The refusal for a row whose item retention has already destroyed.</summary>
        public const string PurgedMessage = "That was destroyed by retention and cannot be restored.";

        /// <summary>The refusal when the account's vault has no room for the item coming back.</summary>
        public const string NoRoomToRestoreMessage = "That account's vault has no room for it. It has to make room first.";

        /// <summary>
        /// Puts one barreled thing back: the actual stored biota for an item row, or the units for a
        /// ledger row. The administrator-facing half of the barrel.
        ///
        /// THE ORDER OF THE STEPS IS WHAT KEEPS A REFUSED RESTORE FROM CONSUMING THE ROW. Everything
        /// that can refuse - the two terminal stamps, readiness, and whether the vault has room -
        /// happens before anything moves, and RestoredAt is stamped only once the item or the units
        /// have provably moved. A row that this method refuses is left exactly as it was found, still
        /// restorable.
        ///
        /// <paramref name="rowId"/> is the account_vault_barrel row id, which is what /vaultrestore
        /// list prints. Named rowId rather than barrelRowId deliberately: this class already has a
        /// private field called barrelRowId holding the barrel CONTAINER's index row, and the two are
        /// unrelated numbers.
        /// </summary>
        internal bool TryRestoreFromBarrel(uint rowId, VaultActor actor, out string failReason)
        {
            AssertOnMutationQueue(nameof(TryRestoreFromBarrel));

            Touch();

            var row = backend.GetAccountVaultBarrel(rowId);

            if (row == null)
            {
                // A single-row read cannot tell "no such row" from "the read failed", so this refuses
                // rather than inferring either. Acting on a row that could not be confirmed is how an
                // item gets restored twice.
                failReason = $"No barrel row {rowId} could be read.";
                return false;
            }

            if (row.AccountId != AccountId)
            {
                // The caller resolved the store from an account name and the row id separately, so
                // nothing upstream guarantees they agree. Restoring into the wrong vault would hand
                // one player another player's item.
                failReason = $"Barrel row {rowId} belongs to account {row.AccountId}, not {AccountId}.";
                return false;
            }

            if (row.RestoredAt != null)
            {
                failReason = AlreadyRestoredMessage;
                return false;
            }

            if (row.PurgedAt != null)
            {
                failReason = PurgedMessage;
                return false;
            }

            lock (stateLock)
            {
                if (!CheckReadyLocked(out failReason))
                    return false;

                // The cap counts ENTRIES (DESIGN 7.3). A ledger restore onto a row the account still
                // holds joins that row and costs nothing, so asking for one there would be a false
                // refusal; everything else costs one.
                var newEntries = row.ItemGuid == null && ledger.TryGetValue(row.Wcid, out var held) && held > 0 ? 0 : 1;

                if (!HasRoomForLocked(newEntries))
                {
                    failReason = NoRoomToRestoreMessage;
                    return false;
                }
            }

            return row.ItemGuid == null
                ? RestoreLedgerFromBarrel(row, actor, out failReason)
                : RestoreItemFromBarrel(row, actor, out failReason);
        }

        private bool RestoreItemFromBarrel(AccountVaultBarrel row, VaultActor actor, out string failReason)
        {
            failReason = null;

            var itemGuid = new ObjectGuid(row.ItemGuid.Value);

            lock (stateLock)
            {
                if (barrel == null || !barrel.InventoryLoaded)
                {
                    failReason = BarrelUnavailableMessage;
                    return false;
                }

                if (!barrel.Inventory.ContainsKey(itemGuid))
                {
                    // The row says the item is in the barrel and it is not. That is the shape a
                    // barreling leaves behind when its row landed and its move did not, and it is
                    // deliberately INERT rather than an error the admin has to clear: nothing is
                    // destroyed, and the row simply cannot be acted on.
                    log.Error($"[VAULT] account {AccountId}: account_vault_barrel row {row.Id} names 0x{row.ItemGuid.Value:X8} ({row.ItemName}) but the barrel does not hold it. Nothing was moved and the row is left open.");
                    failReason = $"Barrel row {row.Id} names an item the barrel does not hold.";
                    return false;
                }
            }

            // Outside stateLock: creating a vault blocks on a biota save, and this store never holds
            // its lock across a database round-trip.
            var vault = FindOrCreateVault();

            if (vault == null)
            {
                failReason = UnavailableMessage;
                return false;
            }

            WorldObject item;

            lock (stateLock)
            {
                if (barrel == null || !barrel.TryRemoveFromInventory(itemGuid, out item))
                {
                    failReason = $"Barrel row {row.Id} names an item the barrel does not hold.";
                    return false;
                }

                if (!vault.TryAddToInventory(item))
                {
                    // Put it straight back. The item must never end up held by nothing, and the row is
                    // still open, so a later attempt can retry once the vault has a slot.
                    if (!barrel.TryAddToInventory(item))
                        log.Error($"[VAULT] ORPHAN: account {AccountId}: could not put 0x{item.Guid.Full:X8} ({item.Name}) back into the barrel after vault 0x{vault.Guid.Full:X8} refused it. It has NOT been destroyed and needs manual recovery.");

                    InvalidateGroupingLocked();

                    failReason = NoRoomToRestoreMessage;
                    return false;
                }

                // Inside the lock, beside the mutation. See groupingMemo's remarks.
                InvalidateGroupingLocked();
            }

            // Bumped the moment the item is physically back in a vault: everything below can throw.
            Interlocked.Increment(ref version);

            var restoredGuid = item.Guid.Full;
            var restoredWcid = item.WeenieClassId;

            world.SaveBiota(item, saved =>
            {
                if (saved)
                    return;

                log.Error($"[VAULT] RESTORE SAVE FAILED for account {AccountId}: 0x{restoredGuid:X8} (wcid {restoredWcid}) is back in the vault in memory but its biota did not persist. Queued for retry.");

                lock (stateLock)
                {
                    if (!pendingDepositSaves.ContainsKey(item))
                        pendingDepositSaves[item] = 0;
                }
            });

            world.SaveBiota(vault);
            world.SaveBiota(barrel);

            StampRestored(row);

            WriteLog(AccountVaultAction.Restore, actor, item.WeenieClassId, item.Guid.Full, item.Name, item.StackSize ?? 1);

            return true;
        }

        private bool RestoreLedgerFromBarrel(AccountVaultBarrel row, VaultActor actor, out string failReason)
        {
            failReason = null;

            var credit = backend.TryAdjustAccountVaultStack(AccountId, row.Wcid, row.Count, out var newCount);

            if (credit == AccountVaultStackAdjustResult.Refused)
            {
                // Provably did not move. The row stays open and the restore can be tried again.
                log.Error($"[VAULT] account {AccountId}: the ledger refused a restore of {row.Count} x wcid {row.Wcid} (barrel row {row.Id}). The row is left open.");
                failReason = UnavailableMessage;
                return false;
            }

            if (credit == AccountVaultStackAdjustResult.Applied)
            {
                ApplyLedgerCount(row.Wcid, newCount);
            }
            else
            {
                // Applied-but-unreadable and Failed are both handled the same way HERE, and the
                // asymmetry with the barreling path is deliberate. Barreling resolves an unknown
                // towards recording it, because the danger there is losing units silently. RESTORING
                // resolves it towards STAMPING THE ROW, because the danger here is the opposite one: a
                // credit that did land, on a row left open, is restored a second time and the account
                // is paid twice.
                log.Error($"[VAULT] LEDGER STATE UNKNOWN: account {AccountId}, wcid {row.Wcid}, delta +{row.Count} (barrel row {row.Id}). A restore credit could not be confirmed. The row IS being stamped restored, because a second restore against it would be a dupe; if the credit did not land, re-credit it by hand from account_vault_log.");

                InvalidateLedger($"a barrel restore of {row.Count} x wcid {row.Wcid} with an unconfirmed outcome");
            }

            Interlocked.Increment(ref version);

            StampRestored(row);

            WriteLog(AccountVaultAction.Restore, actor, row.Wcid, null, row.ItemName, row.Count);

            return true;
        }

        /// <summary>
        /// Retention's half of the barrel: destroys the biota one expired row names, so
        /// <see cref="AccountVaultBarrelReaper"/> can then stamp purged_At.
        ///
        /// Returns TRUE when there is provably nothing left to destroy - the item was destroyed here,
        /// or the row is a ledger barreling with no biota behind it by definition. The caller stamps
        /// purged_At only on true, so a row this refuses is left open and tried again next pass.
        ///
        /// Runs on the store's mutation queue like every other mutation, which is what stops it
        /// racing a restore of the same row.
        /// </summary>
        internal bool TryPurgeFromBarrel(AccountVaultBarrel row, out string failReason)
        {
            AssertOnMutationQueue(nameof(TryPurgeFromBarrel));

            Touch();

            failReason = null;

            if (row == null)
            {
                failReason = "There is no row to purge.";
                return false;
            }

            if (row.ItemGuid == null)
            {
                // A ledger barreling destroyed its units at barrel time - there was never a biota. The
                // purge is the stamp and nothing else.
                return true;
            }

            var itemGuid = new ObjectGuid(row.ItemGuid.Value);

            WorldObject item;

            lock (stateLock)
            {
                if (barrel == null || !barrel.InventoryLoaded)
                {
                    failReason = BarrelUnavailableMessage;
                    return false;
                }

                if (!barrel.TryRemoveFromInventory(itemGuid, out item))
                {
                    // The row names an item the barrel does not hold, which is the inert shape a
                    // barreling leaves behind when its row landed and its move did not. It is REFUSED
                    // rather than stamped: stamping would record that retention destroyed an item that
                    // is in fact still sitting in the player's vault, and the audit row is the one
                    // artifact that has to stay true. The row stays open and an operator has to
                    // resolve it.
                    log.Error($"[VAULT] account {AccountId}: account_vault_barrel row {row.Id} names 0x{row.ItemGuid.Value:X8} ({row.ItemName}) for purging, but the barrel does not hold it. NOT stamping purged_At - the item was never barreled and may still be in the player's vault. This needs an operator.");
                    failReason = $"Barrel row {row.Id} names an item the barrel does not hold.";
                    return false;
                }
            }

            // No InvalidateGroupingLocked and no version bump: grouping walks `vaults`, and the barrel
            // is in neither that list nor GetEntries, so nothing a window can see has changed.
            world.DestroyItem(item);
            world.SaveBiota(barrel);

            return true;
        }

        /// <summary>
        /// Marks a barrel row restored, AFTER the item or the units have provably moved.
        ///
        /// A failed write is logged loudly rather than unwound: the thing is already back, so the row
        /// reading open is a DOUBLE-RESTORE risk, and the only useful response is to make it visible.
        /// Putting the item back into the barrel to match the row would be the worse trade - it would
        /// take away from the player what an administrator has just decided to give them, over a
        /// bookkeeping write.
        /// </summary>
        private void StampRestored(AccountVaultBarrel row)
        {
            row.RestoredAt = DateTime.UtcNow;

            if (!backend.UpdateAccountVaultBarrel(row))
                log.Error($"[VAULT] account {AccountId}: DOUBLE RESTORE RISK - could not stamp restored_At on account_vault_barrel row {row.Id} ({row.ItemName}). The item or units ARE back in the vault while the row still reads open, so a second /vaultrestore of this row would duplicate them. Close the row by hand.");
        }

        #endregion

        #region Grants

        /// <summary>
        /// Fix round 1, F5: <paramref name="canonicalName"/> is the resolved character name, not the
        /// raw <paramref name="granteeName"/> text - a caller that echoes the raw text back to a player
        /// ("bOB may now...") produces a confirmation that does not match what /mule access will later
        /// list ("Bob"), leaving a case-mismatched player unable to find their own grant.
        ///
        /// Fix round 2, F6: this used to resolve the name a SECOND time here, via
        /// <see cref="IAccountVaultWorldSource.TryResolveCharacter"/>, so that TryGrantCore's signature
        /// could be left alone. TryGrantCore's signature WAS widened after all - it now hands back the
        /// name it resolved, plus <paramref name="changed"/> - and the duplicate lookup is gone. The old
        /// arrangement was described here as "redundant but harmless"; that stopped being true once
        /// /mule grant turned out to be uncooldowned, because "harmless" was costing a second character
        /// lookup on every iteration of a macro that anyone could run against any named player.
        ///
        /// <paramref name="changed"/> is false when the grant already existed on identical terms, which
        /// is what lets HandleActionMuleGrant stay silent toward the grantee rather than sending them a
        /// chat line for a grant that did not actually change anything.
        /// </summary>
        public bool TryGrant(Player owner, string granteeName, bool canWithdraw, out string canonicalName, out bool changed, out string failReason)
        {
            var actor = VaultActor.From(owner);

            var ok = false;
            var didChange = false;
            string resolvedName = null;
            string reason = null;

            // RunOnQueue returning false means the work never ran at all, so these locals are still at
            // the values initialized above - which is why they are initialized rather than assigned only
            // inside the lambda.
            if (!RunOnQueue(() => ok = TryGrantCore(actor, granteeName, canWithdraw, out resolvedName, out didChange, out reason)))
                reason = UnavailableMessage;

            canonicalName = ok ? resolvedName : null;
            changed = ok && didChange;
            failReason = reason;
            return ok;
        }

        /// <summary>
        /// MUST run on the mutation queue, like every other mutation. A grant is not just a row write:
        /// <see cref="TryGetAccess"/> re-reads the grant list inside every deposit and withdraw, so a
        /// same-assembly caller running this off the queue would race an in-flight transaction's
        /// authorization decision against the write that changes it.
        ///
        /// <paramref name="canonicalName"/> is the name the world resolved, and is assigned ONLY on the
        /// two returns that report success - every failure path leaves it null, so a caller cannot echo
        /// a name back out of a grant that was refused.
        ///
        /// <paramref name="changed"/> is false when the grant already existed on identical terms.
        /// </summary>
        internal bool TryGrantCore(VaultActor owner, string granteeName, bool canWithdraw,
                                   out string canonicalName, out bool changed, out string failReason)
        {
            AssertOnMutationQueue(nameof(TryGrantCore));

            Touch();

            canonicalName = null;
            changed = false;
            failReason = null;

            if (owner.AccountId == 0 || owner.AccountId != AccountId)
            {
                failReason = "Only the vault's owner can share it.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(granteeName))
            {
                failReason = "Name a character to share your vault with.";
                return false;
            }

            if (!world.TryResolveCharacter(granteeName.Trim(), out var granteeGuid, out var resolvedName, out var granteeAccountId))
            {
                failReason = $"There is no character named {granteeName.Trim()}.";
                return false;
            }

            if (granteeAccountId != 0 && granteeAccountId == AccountId)
            {
                failReason = $"{resolvedName} is on your own account and already has full access.";
                return false;
            }

            // A repeat grant on identical terms is a no-op, not a rewrite. The backend is an UPSERT
            // with no already-granted refusal, so without this an uncooldowned macro produced a write,
            // a permanent audit row and a chat line to the named player on every iteration.
            //
            // A null read here is a read FAILURE, not an empty grant list, and it falls through to the
            // upsert deliberately: writing a grant the owner asked for is the right answer when we
            // cannot tell whether it already exists, and the upsert is idempotent at the database.
            var existing = backend.GetAccountVaultGrants(AccountId)?
                .FirstOrDefault(g => g.GranteeCharacterGuid == granteeGuid);

            if (existing != null && existing.CanWithdraw == canWithdraw)
            {
                canonicalName = resolvedName;
                changed = false;
                return true;
            }

            var row = new AccountVaultGrant
            {
                OwnerAccountId = AccountId,
                GranteeCharacterGuid = granteeGuid,
                GranteeCharacterName = resolvedName,
                CanWithdraw = canWithdraw,
                GrantedAt = DateTime.UtcNow,
            };

            if (!backend.UpsertAccountVaultGrant(row))
            {
                failReason = UnavailableMessage;
                return false;
            }

            WriteLog(AccountVaultAction.Grant, owner, 0, granteeGuid, resolvedName, canWithdraw ? 1 : 0);

            canonicalName = resolvedName;
            changed = true;
            return true;
        }

        /// <summary>
        /// Fix round 1, F5: <paramref name="canonicalName"/> is the grant row's own snapshot name (the
        /// same value <see cref="TryRevokeCore"/> matches against case-insensitively and logs), not the
        /// raw <paramref name="granteeName"/> text - see <see cref="TryGrant(Player, string, bool, out string, out bool, out string)"/>'s
        /// remarks for why this matters. Unlike the grant path, this one still resolves the name
        /// independently rather than taking it out of TryRevokeCore: TryGrantCore's signature was
        /// widened because HandleActionMuleGrant needs its idempotency answer too, and nothing in this
        /// fix needed the same from revoke. The same duplicate-lookup argument does apply here, so this
        /// is a deferred cleanup, not a claim that the split is better.
        /// </summary>
        public bool TryRevoke(Player owner, string granteeName, out string canonicalName, out string failReason)
        {
            var actor = VaultActor.From(owner);

            var wanted = (granteeName ?? string.Empty).Trim();
            var existingGrant = backend.GetAccountVaultGrants(AccountId)?
                .FirstOrDefault(g => string.Equals(g.GranteeCharacterName, wanted, StringComparison.OrdinalIgnoreCase));

            var ok = false;
            string reason = null;

            if (!RunOnQueue(() => ok = TryRevokeCore(actor, granteeName, out reason)))
                reason = UnavailableMessage;

            canonicalName = ok ? existingGrant?.GranteeCharacterName : null;
            failReason = reason;
            return ok;
        }

        /// <summary>
        /// MUST run on the mutation queue - same reason as <see cref="TryGrantCore"/>, and it matters
        /// more here: a revoke that lands between a transaction's authorization check and its write is
        /// the difference between a revoke taking effect and a stranger completing one last withdraw.
        /// </summary>
        internal bool TryRevokeCore(VaultActor owner, string granteeName, out string failReason)
        {
            AssertOnMutationQueue(nameof(TryRevokeCore));

            Touch();

            failReason = null;

            if (owner.AccountId == 0 || owner.AccountId != AccountId)
            {
                failReason = "Only the vault's owner can share it.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(granteeName))
            {
                failReason = "Name a character to stop sharing with.";
                return false;
            }

            var grants = backend.GetAccountVaultGrants(AccountId);

            if (grants == null)
            {
                failReason = UnavailableMessage;
                return false;
            }

            var wanted = granteeName.Trim();

            // Matched on the SNAPSHOT name in the grant row, so a revoke still works after the
            // character has been deleted and PlayerManager can no longer resolve the name.
            var grant = grants.FirstOrDefault(g => string.Equals(g.GranteeCharacterName, wanted, StringComparison.OrdinalIgnoreCase));

            if (grant == null)
            {
                failReason = $"{wanted} does not have access to your vault.";
                return false;
            }

            if (backend.DeleteAccountVaultGrant(AccountId, grant.GranteeCharacterGuid) < 1)
            {
                failReason = UnavailableMessage;
                return false;
            }

            WriteLog(AccountVaultAction.Revoke, owner, 0, grant.GranteeCharacterGuid, grant.GranteeCharacterName, 0);

            return true;
        }

        #endregion

        #region Reaping (R2)

        /// <summary>
        /// Destroys a vault container, and ONLY if it is provably empty.
        ///
        /// The trap this closes in one line: an UNLOADED container is indistinguishable from an empty
        /// one. Inventory.Count is 0 for both, so a reap that checks only the count destroys a vault
        /// whose contents simply have not arrived yet, and Destroy() cascades into every child
        /// (WorldObject.cs:881-885). Worse, dynamic guids return to the pool after 360 minutes
        /// (GuidManager.cs:126), after which a recycled container guid re-parents any orphaned
        /// children into somebody else's container.
        ///
        /// Nothing calls this automatically, and that is deliberate: DESIGN 7.1's free-slot insertion
        /// makes reaping unnecessary, because a hole left by a withdrawal is refilled by the next
        /// deposit. It exists as the single audited place a vault may ever be destroyed, so that any
        /// future maintenance path has one correct implementation to call rather than writing a second
        /// one.
        ///
        /// THE BARREL IS NOT REAPABLE, and it is out of reach here by construction rather than by a
        /// guard: <see cref="barrel"/> is not a member of <see cref="vaults"/>, so no caller iterating
        /// the vault list can ever hand it to this method, and <see cref="vaultRowIds"/> carries no
        /// entry for it, so a caller that named it explicitly would fall out at the row-id lookup. An
        /// empty barrel is not an empty vault: reaping it would destroy the container the next
        /// barreling needs plus the index row that makes it findable, and it can be empty at any
        /// moment simply because the retention sweep has just run.
        /// </summary>
        internal bool TryReapEmptyVault(Container vault)
        {
            if (vault == null)
                return false;

            lock (stateLock)
            {
                if (!TryEnsureLoadedLocked())
                    return false;

                if (!vault.InventoryLoaded)
                    return false;

                if (vault.Inventory.Count != 0)
                    return false;

                if (!vaultRowIds.TryGetValue(vault.Guid.Full, out var rowId))
                    return false;

                if (!backend.DeleteAccountVault(rowId))
                {
                    log.Error($"[VAULT] account {AccountId}: could not delete account_vault row {rowId}; leaving vault 0x{vault.Guid.Full:X8} in place.");
                    return false;
                }

                vaults.Remove(vault);
                vaultRowIds.Remove(vault.Guid.Full);

                // The reaped vault is provably empty, so no group loses a member here - but the memo
                // must still not outlive the vault list it was built from. Inside the lock, like every
                // other invalidation in this class.
                InvalidateGroupingLocked();
            }

            // The index row is gone, so this guid is no longer a vault container and must not keep
            // suppressing whatever recycles it.
            AccountVaultSpawnFilter.Unregister(vault.Guid.Full);

            world.DestroyItem(vault);

            // Warn, not Debug. Nothing calls this today; if anyone ever wires it to a timer, the one
            // thing that must not happen quietly is a vault container being destroyed. This line is
            // what turns "every vault on the account is gone" from an unexplainable report into a
            // searchable one.
            log.Warn($"[VAULT] account {AccountId}: reaped empty vault container 0x{vault.Guid.Full:X8}.");

            // A reaped vault is empty by the guard above, so it contributed no row and this cannot
            // actually change a window's contents. Bumped anyway: the cost is one wasted rebuild, and
            // the rule <see cref="Version"/> states - "every mutation that can change what GetEntries
            // returns" - is worth more than the saving, because the next edit to this method is the one
            // that would make the exemption wrong. (An unqualified "every mutation bumps" would be the
            // wrong rule to invoke: TryGrantCore and TryRevokeCore are mutations and correctly do not
            // bump, because a grant change alters who may look, never what a window would list.)
            Interlocked.Increment(ref version);

            return true;
        }

        #endregion

        #region Audit

        private void WriteLog(AccountVaultAction action, VaultActor actor, uint wcid, uint? itemGuid, string itemName, long count)
        {
            var row = new AccountVaultLog
            {
                OwnerAccountId = AccountId,
                ActorCharacterGuid = actor.CharacterGuid,
                ActorCharacterName = string.IsNullOrEmpty(actor.Name) ? "<unknown>" : actor.Name,
                Action = (int)action,
                Wcid = wcid,
                ItemGuid = itemGuid,
                ItemName = string.IsNullOrEmpty(itemName) ? "<unknown>" : itemName,
                Count = count,
                Timestamp = DateTime.UtcNow,
            };

            // A failure here is logged and swallowed by the DAO on purpose: refusing a player's deposit
            // because the audit write failed would turn a logging outage into a storage outage.
            backend.AddAccountVaultLog(row);
        }

        #endregion

        public override string ToString()
        {
            // The barrel is reported separately from the vault count, not folded into it: an operator
            // reading this line has to be able to see that a container exists which holds nothing the
            // player can reach.
            return $"AccountVaultStore(account {AccountId}, {vaults.Count} vaults, {ledger.Count} ledger rows, barrel {(barrel != null ? "loaded" : "none")})";
        }
    }

    /// <summary>
    /// Keeps vault containers out of the live world when a landblock activates. The residual half of
    /// risk R1: DESIGN 7.2 requires a reserved landblock "that is never activated for players", and
    /// until this existed NOTHING enforced that.
    ///
    /// The path it closes, clause by clause. Landblock.Init calls SpawnDynamicShardObjects, which calls
    /// ShardDatabase.GetDynamicObjectsByLandblock. That query selects biotas with a Location position,
    /// an ObjCellId inside the landblock, a dynamic guid and a matching Instance, then drops anything
    /// carrying a Container or Wielder IID. A vault container matches EVERY clause - having no
    /// Container and no Wielder is the entire premise of R1 - so without this filter every account's
    /// vault container is AddWorldObject'd into the world as an ordinary backpack. That is a live theft
    /// and dupe surface: a second in-memory WorldObject exists for a guid the store also holds, and on
    /// landblock unload SaveDB writes the landblock's copy over the store's.
    ///
    /// Shaped after WorldEventOrphanFilter, which runs immediately before it, with ONE deliberate
    /// difference that must never be "tidied up" into consistency: that filter DELETES what it drops,
    /// and this one must never delete anything. What this drops is live player property. The correct
    /// outcome is that it stays in the shard, untouched and reachable through the vendor, and merely
    /// does not enter the world.
    ///
    /// Two independent predicates, because neither covers the other:
    ///
    /// 1. The landblock is a reserved vault landblock. This is the one that carries the guarantee: it
    ///    holds for every vault container ever created, whether or not this process has heard of it.
    ///    Both the currently configured value and the registered default count, because a config change
    ///    leaves older containers behind in the previous landblock.
    /// 2. The biota's guid is a known vault container. The set is SEEDED FROM THE WHOLE account_vault
    ///    TABLE once at process start (<see cref="Seed"/>, called by AccountVaultManager.Initialize),
    ///    and maintained incrementally after that by the store. This is the one that covers a container
    ///    the landblock predicate cannot see: one created while account_vault_landblock pointed at a
    ///    landblock that is now neither the configured value nor the registered default, and one whose
    ///    Location was moved OUT of a reserved landblock by the careless content edit DESIGN 7.2 names.
    ///
    /// Seeding is one query per process, never per activation. If it FAILS the server still boots -
    /// refusing to boot over this would trade a contained risk for a certain outage - and predicate 1
    /// carries the guarantee alone until AccountVaultManager's periodic retry succeeds. The failure is
    /// logged at ERROR on every retry, and <see cref="IsSeeded"/> reports it.
    ///
    /// Never throws. A landblock that fails to load is an outage; on any failure this hands back the
    /// unfiltered list, exactly as the orphan sweep does.
    /// </summary>
    public static class AccountVaultSpawnFilter
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// Vault container guids this process has created or loaded. Small - one entry per vault
        /// container, four bytes each - and read on the landblock activation path, so it is guarded by
        /// its own lock rather than by any store's state lock.
        /// </summary>
        private static readonly HashSet<uint> knownContainers = new HashSet<uint>();

        private static readonly object knownLock = new object();

        private static volatile bool seeded;

        /// <summary>
        /// True once <see cref="Seed"/> has succeeded. While false the guid predicate covers only the
        /// accounts this process has touched, and the landblock predicate is carrying the whole
        /// guarantee on its own.
        /// </summary>
        public static bool IsSeeded => seeded;

        /// <summary>
        /// Loads the full set of vault container guids, once, at process start. This is what closes
        /// the gap the two other mechanisms cannot: a container created while
        /// account_vault_landblock pointed at some third landblock is covered by neither the current
        /// config nor the registered default, and the incremental registry only learns about accounts
        /// this process has touched, which landblock activation normally precedes.
        ///
        /// A NULL argument means the READ FAILED and is NOT treated as "no vaults exist". It leaves
        /// <see cref="IsSeeded"/> false, keeps every guid already registered, and reports false so the
        /// caller can log it and retry. Treating it as an empty set would be the silent version of the
        /// exact failure this whole class exists to prevent.
        ///
        /// Additive, never destructive: a re-seed after a failed one merges rather than replaces, so a
        /// retry cannot drop a guid that <see cref="Register"/> learned in the meantime.
        /// </summary>
        public static bool Seed(IEnumerable<uint> containerGuids)
        {
            if (containerGuids == null)
                return false;

            lock (knownLock)
            {
                foreach (var guid in containerGuids)
                    knownContainers.Add(guid);
            }

            seeded = true;

            return true;
        }

        /// <summary>How many vault container guids are currently known. For logging and tests.</summary>
        public static int KnownCount
        {
            get
            {
                lock (knownLock)
                    return knownContainers.Count;
            }
        }

        /// <summary>Called by the store when a container becomes a registered vault.</summary>
        internal static void Register(uint containerGuid)
        {
            lock (knownLock)
                knownContainers.Add(containerGuid);
        }

        /// <summary>Called by the store when a vault's index row is deleted, so a recycled guid is free again.</summary>
        internal static void Unregister(uint containerGuid)
        {
            lock (knownLock)
                knownContainers.Remove(containerGuid);
        }

        public static bool IsKnownVaultContainer(uint containerGuid)
        {
            lock (knownLock)
                return knownContainers.Contains(containerGuid);
        }

        /// <summary>
        /// True for a landblock that vault container Locations point into: the currently configured
        /// account_vault_landblock, or the value it ships with. Both, because containers created before
        /// a config change keep the landblock they were created in, and a landblock 0 never counts -
        /// that is the refused fallback value, not a reservation.
        /// </summary>
        public static bool IsReservedVaultLandblock(ushort landblockId)
        {
            if (landblockId == 0)
                return false;

            return landblockId == AccountVaultStore.ConfiguredVaultLandblock()
                || landblockId == AccountVaultStore.RegisteredVaultLandblock();
        }

        /// <summary>
        /// The predicate, pure apart from the two static reads above. Public so a test can exercise it
        /// without a landblock.
        /// </summary>
        public static bool ShouldSuppress(ushort landblockId, ShardBiota biota)
        {
            if (biota == null)
                return false;

            return IsReservedVaultLandblock(landblockId) || IsKnownVaultContainer(biota.Id);
        }

        /// <summary>
        /// The landblock-facing entry point. Returns the biotas that may enter the world; anything
        /// suppressed is logged and LEFT IN THE SHARD.
        ///
        /// The no-suppression case - every landblock activation on a correctly configured server - hands
        /// back the SAME list instance rather than a copy, so the normal path costs one scan, one
        /// integer comparison per biota and no allocation.
        /// </summary>
        public static List<ShardBiota> Filter(ushort landblockId, uint instance, List<ShardBiota> dynamics)
        {
            try
            {
                if (dynamics == null || dynamics.Count == 0)
                    return dynamics;

                var reserved = IsReservedVaultLandblock(landblockId);

                var suppressedCount = 0;

                foreach (var biota in dynamics)
                {
                    if (biota != null && (reserved || IsKnownVaultContainer(biota.Id)))
                        suppressedCount++;
                }

                if (suppressedCount == 0)
                    return dynamics;

                var kept = new List<ShardBiota>(dynamics.Count - suppressedCount);
                var suppressedIds = new List<uint>(suppressedCount);

                foreach (var biota in dynamics)
                {
                    if (biota != null && (reserved || IsKnownVaultContainer(biota.Id)))
                        suppressedIds.Add(biota.Id);
                    else
                        kept.Add(biota);
                }

                var guids = string.Join(",", suppressedIds.Select(id => $"0x{id:X8}"));

                // ERROR, not Info. On a correctly configured server this line never appears: the
                // reserved landblock is never activated and no vault container sits anywhere else. If it
                // does appear, either account_vault_landblock points at a live landblock or something
                // has moved a vault container, and both want investigating.
                log.Error($"[VAULT] landblock 0x{landblockId:X4} instance {instance} suppressed {suppressedIds.Count} vault container biota(s) from entering the world; they remain in the shard untouched. reserved={reserved} seeded={seeded} guids={guids}");

                return kept;
            }
            catch (Exception ex)
            {
                log.Error($"[VAULT] landblock 0x{landblockId:X4} instance {instance} vault spawn filter failed; loading the landblock unfiltered", ex);

                return dynamics;
            }
        }
    }

    /// <summary>
    /// The three facts the store needs about whoever is acting: which account they belong to, which
    /// character they are, and what to write in the audit row.
    ///
    /// Exists so that authorization and auditing can be exercised without constructing a Player, which
    /// needs a Session, a Character and three live databases. The public API still takes a Player;
    /// this is what it immediately becomes.
    /// </summary>
    /// <summary>
    /// One account's saved mule look: the donor wcid, and the donor's name as it read when the look
    /// was saved.
    ///
    /// <see cref="Name"/> is a SNAPSHOT and is never refreshed. It exists for logging and for an
    /// operator reading account_mule_form; nothing in the summon path keys on it, and the donor weenie
    /// named by <see cref="Wcid"/> may since have been renamed or deleted outright - which the summon
    /// path treats as "no form", never as an error.
    /// </summary>
    internal readonly struct MuleFormLook
    {
        public uint Wcid { get; }

        public string Name { get; }

        public MuleFormLook(uint wcid, string name)
        {
            Wcid = wcid;
            Name = name;
        }
    }

    internal readonly struct VaultActor
    {
        public uint AccountId { get; }

        public uint CharacterGuid { get; }

        public string Name { get; }

        public VaultActor(uint accountId, uint characterGuid, string name)
        {
            AccountId = accountId;
            CharacterGuid = characterGuid;
            Name = name;
        }

        public static VaultActor From(Player player)
        {
            if (player == null)
                return default;

            // A null Account leaves AccountId at 0, which matches no real account and therefore denies
            // rather than granting.
            return new VaultActor(player.Account?.AccountId ?? 0, player.Guid.Full, player.Name);
        }
    }
}
