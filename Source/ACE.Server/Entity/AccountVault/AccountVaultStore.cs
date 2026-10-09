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

        /// <summary>
        /// One counted item-class row as this store holds it in memory, with its payload already
        /// parsed.
        ///
        /// THE PARSE HAPPENS ONCE, AT LOAD. The stored canonical form is the only payload
        /// account_vault_class carries, and both the vendor view and every withdraw need the override
        /// set that is inside it. Parsing per read would put a string walk on the panel rebuild and a
        /// second one on every withdraw, and - worse - would give two callers two independent chances
        /// to disagree about what the row says. A row whose text will not parse, or whose denormalized
        /// wcid or band disagrees with it, never enters this map at all.
        /// </summary>
        private sealed class VaultClassRow
        {
            public string ClassKey;
            public uint Wcid;
            public long Count;
            public long TotalValue;
            public string CanonicalForm;
            public VaultItemClassOverrides Overrides;

            /// <summary>The row's own PropertyString.Name override, or null when it carries none.</summary>
            public string DisplayName => Overrides?.GetString(PropertyString.Name);
        }

        /// <summary>
        /// class key -> the counted class row. Mirrors account_vault_class; only rows with a positive
        /// COUNT are kept, exactly as <see cref="ledger"/> keeps only non-zero rows, because
        /// <see cref="EntryCountLocked"/> and <see cref="EnumerateEntriesLocked"/> must agree about
        /// which rows the panel draws.
        ///
        /// Guarded by <see cref="stateLock"/>, like <see cref="ledger"/>.
        /// </summary>
        private readonly Dictionary<string, VaultClassRow> classes = new Dictionary<string, VaultClassRow>(StringComparer.Ordinal);

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
        /// collocated with the mutation under the same lock, and on most paths it is not: those
        /// mutations apply their change to <see cref="vaults"/> inside a `lock (stateLock)` block,
        /// RELEASE that lock, and only then run `Interlocked.Increment(ref version)` - with a database
        /// round-trip in between on WithdrawItem, WithdrawGroup and TryReturnWithdrawn.
        /// <see cref="DepositToVault"/> is the ONE exception and is collocated: its bump happens under
        /// stateLock in <see cref="AddStoredItemToGroupingLocked"/>, which is a precondition of that
        /// path maintaining the memo rather than dropping it. <see cref="GetEntries(int, int)"/> and
        /// <see cref="EntryCount"/> are plain locked readers and do NOT go through the mutation queue,
        /// so a reader can acquire stateLock inside that window and observe mutated vaults carrying an
        /// unchanged version. Keyed on the version alone the memo would then hand back the PRE-mutation
        /// group list, including WorldObject references to items already removed from their container.
        ///
        /// Two defences, and both are wanted:
        ///
        /// 1. Every locked section that mutates vaults or their inventories brings the memo up to date
        ///    from INSIDE that same lock - <see cref="InvalidateGroupingLocked"/> on every path, and
        ///    <see cref="AddStoredItemToGroupingLocked"/> on the deposit path, which maintains it in
        ///    place instead. This is the defence that closes the window described above, and it closes
        ///    it completely: every memo reader also runs under stateLock, so the lock's happens-before
        ///    guarantees any later reader sees either the nulled memo or the maintained one, never a
        ///    memo describing vaults as they were before the mutation, whether or not Version has been
        ///    bumped yet.
        /// 2. The memo also records the vault count and the total stored-item count it was built from,
        ///    and <see cref="GroupedStoredItemsLocked"/> rechecks both against live state on every call.
        ///    This is purely a SAFETY NET for a future mutation site that forgets defence 1: an
        ///    enumeration of call sites rots, a live structural check does not. It is not what closes
        ///    the window today.
        ///
        /// Keep both. Defence 1 is exact but depends on every future author remembering it; defence 2
        /// needs nobody to remember anything but cannot see a membership change that leaves both counts
        /// equal.
        ///
        /// COST BOUND, so the next reader does not rediscover it as a bug. This list is a
        /// <see cref="List{T}"/> and <see cref="AddStoredItemToGroupingLocked"/>'s reposition branch does
        /// a <see cref="List{T}.Remove"/> on it, which is a reference-equality IndexOf plus a RemoveAt
        /// shift: O(g) in the number of GROUPS, and g trends to the stored-item count for a vault of
        /// ungroupable items. That is a KNOWN and DELIBERATELY UNFIXED bound, not an oversight.
        ///
        /// It was measured rather than assumed, on the reposition grid in VaultDepositScalingProbeTests,
        /// which drives one arrival per distinct bucket so that EVERY deposit takes the branch and prints
        /// repositions=N/N to prove it. Across V = 25 to 2000 of ungroupable filler - an 80x range, and
        /// g tracks V there because each filler is its own group - the per_deposit column is FLAT within
        /// run-to-run noise, 0.056 to 0.090 ms with no monotone trend in either filler shape, while
        /// bytes_per_deposit moves 16524 to 16684, or +1.0%. For scale, the same grid's build column puts
        /// one VaultCollapse.AreGroupable diff at roughly 8 to 10 us (salvage build 16.8 to 19.4 ms over
        /// 2000 items), so the reposition term does not resolve against even a single diff. Read the
        /// bound as the +1.0% allocation, which is the only growth that measured at all: the time growth
        /// is below this harness's noise floor, which is NOT the same as proven zero.
        ///
        /// A linked list or a group-to-index reverse map would make it O(1) and neither is worth the
        /// complexity here - the reverse map in particular would trade an O(g) memmove for O(g) index
        /// bookkeeping on every ordered insert, since an insert shifts the position of every later group.
        ///
        /// The branch is also self-limiting: a deposit appends at the end of its vault, so once the first
        /// arrival has pulled a group into the earliest vault, later arrivals for that same group sort
        /// after it and take the O(1) path. Making every deposit reposition needs one arrival per
        /// distinct bucket, which is why the probe has to construct it deliberately.
        /// </summary>
        private List<List<WorldObject>> groupingMemo;

        /// <summary>
        /// The bucket index <see cref="GroupedStoredItemsLocked"/> builds on its way to
        /// <see cref="groupingMemo"/>, RETAINED rather than discarded so that
        /// <see cref="AddStoredItemToGroupingLocked"/> can place one new item without re-walking every
        /// stored biota. Bucket key -> the groups open in that bucket, same shape the build uses.
        ///
        /// Its lifetime is exactly <see cref="groupingMemo"/>'s: both are written together under
        /// <see cref="stateLock"/> and both are dropped together by
        /// <see cref="InvalidateGroupingLocked"/>. The incremental path refuses to run unless BOTH are
        /// present, so a half-dropped pair can never be maintained into a wrong answer.
        ///
        /// The lists it holds are the SAME List instances <see cref="groupingMemo"/> holds, not copies -
        /// that is what lets a member insert be seen through both views at once. Nothing hands either
        /// structure out: <see cref="GetEntries(int, int)"/> and the cap check read them under the lock
        /// and project what they need.
        /// </summary>
        private Dictionary<(uint, int, int), List<List<WorldObject>>> groupingBucketsMemo;

        private long groupingMemoVersion = -1;

        private int groupingMemoVaultCount = -1;

        private int groupingMemoItemCount = -1;

        /// <summary>
        /// How many times the grouping memo has been REBUILT, as opposed to served from the memo, over
        /// this store's lifetime. Always read and written under <see cref="stateLock"/>.
        ///
        /// Test instrumentation, and it lives on the store rather than on a seam because nothing else
        /// can see it: the rebuild walks every stored biota and calls VaultCollapse.AreGroupable, both
        /// of which are static and invisible to <see cref="IAccountVaultWorldSource"/>. It is the
        /// number that tells a scaling test whether a deposit's cost depends on how much the account
        /// already holds, which no functional assertion can distinguish from a deposit that is merely
        /// correct.
        /// </summary>
        internal int GroupingRebuilds
        {
            get
            {
                lock (stateLock)
                    return groupingRebuilds;
            }
        }

        private int groupingRebuilds;

        /// <summary>
        /// How many times an incremental add has had to REPOSITION a group, because the arriving item
        /// sorted ahead of the group's representative and a group is emitted at its representative's
        /// position. Always read and written under <see cref="stateLock"/>.
        ///
        /// Test instrumentation, and it exists because that branch is the one part of
        /// <see cref="AddStoredItemToGroupingLocked"/> that is NOT O(1) - it is a
        /// <see cref="List{T}.Remove"/> plus an insert over <see cref="groupingMemo"/>. A cost test has
        /// to be able to prove its deposits actually reached that branch, or a flat measurement means
        /// only that the branch never ran. See VaultDepositScalingProbeTests' reposition grid.
        /// </summary>
        internal int GroupRepositions
        {
            get
            {
                lock (stateLock)
                    return groupRepositions;
            }
        }

        private int groupRepositions;

        /// <summary>
        /// The last DISPLAY grouping of <see cref="classes"/>: the class rows bucketed by
        /// <see cref="VaultCollapse.ClassDisplayGroupKey"/>, in the order the panel draws them.
        /// Guarded by <see cref="stateLock"/> like everything else it covers.
        ///
        /// It earns its place for exactly the reason <see cref="groupingMemo"/> does, and a little more
        /// sharply: <see cref="EntryCountLocked"/> runs on every deposit's cap check, and rebuilding
        /// this walks every class row the account holds. An account with a few thousand rows would
        /// otherwise re-derive a few thousand display keys per deposit.
        ///
        /// KEYED ON THE VERSION AND MORE, never on the version alone, for the same reason the stored-
        /// item memo is. The real defence is <see cref="InvalidateClassGroupingLocked"/>, which is
        /// called from inside every locked section that writes to <see cref="classes"/> - there are
        /// only two, <see cref="LoadClassRowsLocked"/> and <see cref="ApplyClassCountLocked"/>. The row
        /// count check beside it is the safety net for a third one added later: an enumeration of call
        /// sites rots, a live structural check does not.
        /// </summary>
        private List<List<VaultClassRow>> classGroupMemo;

        /// <summary>Display key -> index into <see cref="classGroupMemo"/>, so a cap check is a hash lookup rather than a scan.</summary>
        private Dictionary<string, int> classGroupIndexMemo;

        /// <summary>
        /// Parallel to <see cref="classGroupMemo"/>: each group's <see cref="VaultCollapse.ClassDisplayId"/>,
        /// built with it and dropped with it, so the per-read enumeration never re-hashes a key.
        /// </summary>
        private List<string> classGroupIdMemo;

        private long classGroupMemoVersion = -1;

        private int classGroupMemoRowCount = -1;

        /// <summary>
        /// How many times the class display grouping has been REBUILT rather than served from the memo.
        /// Test instrumentation, and the counterpart of <see cref="GroupingRebuilds"/>: it is the only
        /// way a scaling test can tell a deposit whose cost is constant from one that walks every class
        /// row the account holds.
        /// </summary>
        internal int ClassGroupingRebuilds
        {
            get
            {
                lock (stateLock)
                    return classGroupingRebuilds;
            }
        }

        private int classGroupingRebuilds;

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

        /// <summary>
        /// The same flag for <see cref="classes"/>: set when a class mutation COMMITTED but its
        /// resulting count could not be read back. While it is set, <see cref="classes"/> is known to
        /// be stale for at least one class and must not be served.
        ///
        /// Kept SEPARATE from <see cref="ledgerReloadPending"/> rather than folded into it, because
        /// the two reloads read different tables: one unknown class outcome would otherwise force a
        /// re-read of the whole stack ledger as well, and a stack read that then failed would refuse a
        /// store whose stack ledger was never in doubt.
        ///
        /// Guarded by <see cref="stateLock"/>, like everything else it protects.
        /// </summary>
        private bool classReloadPending;

        /// <summary>
        /// The open coalescing deposit window, or null. See <see cref="VaultDepositWindow"/> for what a
        /// window defers and why the deposit path may tolerate the two flags above while one is open.
        ///
        /// Guarded by <see cref="stateLock"/>, like everything else it protects. Opened and closed only
        /// from the mutation queue, so the deposit path reads it on the one thread that can have written
        /// it; every cross-thread read (the two reload routines) holds the lock.
        /// </summary>
        private VaultDepositWindow depositWindow;

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
        /// Vault containers whose own biota needs re-saving because a deposit changed them, flushed
        /// once by <see cref="Drain"/> when the mutation queue empties rather than once per item.
        ///
        /// WHY THIS IS SAFE, and it is the change on this path a reviewer should challenge hardest.
        /// The container save does NOT record membership. Containment lives on the CHILD - the item's
        /// own ContainerId - which is why DepositToVault saves the item first and says so in its own
        /// remark, and why that save is the one queued for retry in <see cref="pendingDepositSaves"/>
        /// when it fails. What the container's biota carries is the container's own derived state
        /// (EncumbranceVal, Value), which Container.SortWorldObjectsIntoInventory recomputes from the
        /// children it finds on load. So a crash in the window this widens loses a container's
        /// aggregate properties, never an item: the item's row still points at the vault, and the
        /// vault still finds it.
        ///
        /// WHY IT IS WORTH IT. These are SAME-ID saves, and SerializedShardDatabase explicitly cannot
        /// batch those - a same-id duplicate inside a would-be batch is held over and run on its own -
        /// so a 512-item sale queued 512 separate container saves ahead of every other player's shard
        /// work, logins included.
        ///
        /// Guarded by <see cref="stateLock"/>. Drained under <see cref="drainLock"/>, which is the same
        /// exclusion the queued work itself runs under, so the flush cannot interleave with a deposit.
        /// A HashSet because one drain can deposit into several vaults and each needs exactly one save.
        /// </summary>
        private readonly HashSet<Container> dirtyVaults = new HashSet<Container>();

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

        /// <summary>
        /// How far the most recent TryDeposit got past its point of no return. Valid on the mutation
        /// queue immediately after a TryDeposit returns OR THROWS, and only then: the next deposit
        /// resets it before doing anything else.
        ///
        /// This is the one piece of deposit state a caller cannot reconstruct for itself. A refusal and
        /// a throw both reach the caller without saying which side of the commit they happened on, and
        /// the market's delivery must know, because handing a landed item back to its seller duplicates
        /// it (see VaultMarketItemStore.TryGiveToBuyer). Written only by the mutation queue, so a plain
        /// field is enough.
        /// </summary>
        internal VaultDepositCommit LastDepositCommit => depositCommit;

        private VaultDepositCommit depositCommit;

        internal AccountVaultStore(uint accountId, IAccountVaultBackend backend = null, IAccountVaultWorldSource world = null)
        {
            AccountId = accountId;

            this.backend = backend ?? new ShardAccountVaultBackend();
            this.world = world ?? new ShardAccountVaultWorldSource();

            LastTouchedUnixTime = Time.GetUnixTime();

            // Outside stateLock by construction - nothing holds it yet - which is the whole reason the
            // capacity bonuses are computed here rather than in TryEnsureLoadedLocked. See
            // RefreshCapacityBonuses for the lock-ordering rule this obeys.
            RefreshCapacityBonuses();
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
        /// <see cref="RefreshCapacityBonuses"/> - and every reader inside the lock sees only this field.
        /// Same "compute once, cache for the session" shape as Player_AltCharacterBonus's
        /// altCharacterBonusThreshold.
        ///
        /// Written and read as a plain int: a torn read is not possible for an aligned 32-bit field, and
        /// the worst a race between a refresh and a deposit can do is enforce the previous bonus for one
        /// deposit, which is a delayed benefit and never a lost one.
        /// </summary>
        private int muleSpaceBonusEntries;

        /// <summary>
        /// Extra entries bought through /mule upgrade, cached for the exact same reason
        /// <see cref="muleSpaceBonusEntries"/> is: <see cref="HasRoomForLocked"/> runs under
        /// <see cref="stateLock"/> and AccountCapacityUpgradeManager.GetCount takes its own per-account
        /// gate, so nesting the two would introduce a lock order this class has nowhere else. Computed
        /// once in the constructor and again from <see cref="RefreshCapacityBonuses"/>.
        /// </summary>
        private int capacityUpgradeBonusEntries;

        /// <summary>
        /// The cap this account actually gets: the base cap, plus 100 entries per AugmentationMuleSpace
        /// held anywhere on the account, plus 100 entries per /mule upgrade the account has bought. EVERY
        /// enforcement site and every player-facing "N of M entries" line reads this, never
        /// <see cref="BaseEntryCap"/>.
        ///
        /// Clamped so it can neither overflow nor fall below the base: a bonus is a bonus, so a corrupt or
        /// absurd property value can only ever fail to help.
        ///
        /// Safe to read from inside <see cref="stateLock"/> - it touches only <see cref="BaseEntryCap"/>
        /// (a PropertyManager read) and the two cached bonus fields, never PlayerManager or
        /// AccountCapacityUpgradeManager.
        /// </summary>
        public int EffectiveEntryCap
        {
            get
            {
                var baseCap = BaseEntryCap;

                var bonus = (long)muleSpaceBonusEntries + capacityUpgradeBonusEntries;

                if (bonus <= 0)
                    return baseCap;

                var effective = baseCap + bonus;

                if (effective > int.MaxValue)
                    return int.MaxValue;

                return (int)effective;
            }
        }

        /// <summary>
        /// Recomputes both <see cref="muleSpaceBonusEntries"/> and <see cref="capacityUpgradeBonusEntries"/>
        /// from the account's characters and its purchased upgrades. Called from the constructor, again
        /// by the Custom Dreamweave Augmentation broker's grant path (through
        /// AccountVaultManager.RefreshCapacityBonuses) after a MuleSpace trade, and again by
        /// CapacityUpgradeBroker after a /mule upgrade purchase, so either kind of bonus is reflected
        /// immediately rather than on the next store load.
        ///
        /// MUST be called with <see cref="stateLock"/> NOT held - it reaches PlayerManager, which takes its
        /// own read lock. This method deliberately takes no lock of its own for that reason.
        ///
        /// A missed call is a delayed benefit, never a lost one: the next store this account loads
        /// recomputes it from scratch.
        /// </summary>
        public void RefreshCapacityBonuses()
        {
            SetMuleSpaceBonusEntries(CustomAugBroker.AccountAugCount(AccountId, PropertyInt.AugmentationMuleSpace));
            SetCapacityUpgradeBonusEntries(AccountCapacityUpgradeManager.GetCount(AccountId, CapacityUpgradeKind.MuleVault));
        }

        /// <summary>
        /// Sets the cached bonus from an already-counted number of augmentations. Split out from
        /// <see cref="RefreshCapacityBonuses"/> so a unit test can exercise <see cref="EffectiveEntryCap"/>'s
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
        /// Sets the cached /mule upgrade bonus from an already-counted number of upgrades. Split out from
        /// <see cref="RefreshCapacityBonuses"/> for the same testability reason as
        /// <see cref="SetMuleSpaceBonusEntries"/>. Visible to ACE.Server.Tests via InternalsVisibleTo
        /// (ACE.Server.csproj:15).
        /// </summary>
        internal void SetCapacityUpgradeBonusEntries(int upgradeCount)
        {
            if (upgradeCount <= 0)
            {
                capacityUpgradeBonusEntries = 0;
                return;
            }

            var bonus = (long)upgradeCount * CapacityUpgrades.CapacityUpgradePricing.MuleEntriesPerUpgrade;

            capacityUpgradeBonusEntries = bonus > int.MaxValue ? int.MaxValue : (int)bonus;
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
            // Counted class rows are counted too, and they have to be: EnumerateEntriesLocked yields
            // them, so the panel draws them, and a cap that did not count them would let an account
            // draw more rows than the client is asked to render - which is exactly the number this cap
            // is documented to bound.
            //
            // BY DISPLAY GROUP, NEVER BY ROW, and that is the 2026-09-25 fix. The class partition is
            // strictly finer than the display partition - a class key carries the RAW
            // (ItemWorkmanship, NumItemsInMaterial) pair while the panel buckets their QUOTIENT - so
            // `classes.Count` counted rows the panel draws as one line. Folding a vault then INFLATED
            // its entry count instead of reducing it, and pushed accounts past the cap over rows
            // nobody could see. This method and EnumerateEntriesLocked must return the same number of
            // lines; VaultClassDisplayGroupTests asserts exactly that.
            return GroupedStoredItemsLocked().Count + ledger.Count(kvp => kvp.Value > 0) + ClassDisplayGroupsLocked().Count;
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

            // OUTSIDE the lock, for the same reason IsPristine above is, and only when the item is not
            // already going to the stack ledger: TryDescribeClass takes the item's own
            // BiotaDatabaseLock read lock, which is NoRecursion.
            //
            // BOTH keys come back from the one predicate call. The cap rule needs the DISPLAY key as
            // well as the class key now, and deriving it later would mean a second TryDescribeClass -
            // the most expensive thing on this path.
            string classKey = null;
            string classDisplayKey = null;

            if (!pristine)
                TryDescribeClassKeys(item, out classKey, out classDisplayKey);

            lock (stateLock)
            {
                // Gated on READY, exactly as EntryCount is, and for the same reason: an unloaded store
                // reports an empty ledger and empty vaults, so a deposit that really would join a row
                // the account already draws is counted as adding a new one. The conservative answer for
                // a store that cannot be read is "yes, it adds an entry" - it can only refuse a deposit
                // that would have been free, never admit one past the cap.
                if (!CheckReadyLocked(out _))
                    return true;

                return AddsEntryLocked(pristine, item, classKey, classDisplayKey);
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
        ///
        /// THE THIRD ARM is a classifiable item whose class the account ALREADY holds a row for, and it
        /// has the same shape and the same reason as the other two: that deposit increments a row the
        /// panel is already drawing, so it costs no entry and the cap may not refuse it. Without it an
        /// account at its cap could not add a 501st salvage bag to a class it already holds 500 of,
        /// which is precisely the hoarding case the counted tier exists to make free.
        ///
        /// THE THIRD ARM IS TESTED ON THE DISPLAY GROUP, NOT ON THE CLASS KEY, and that is the
        /// 2026-09-25 fix. A freshly salvaged bag almost always carries a novel raw
        /// (ItemWorkmanship, NumItemsInMaterial) pair, so it reads as a brand-new CLASS even when the
        /// panel is already drawing its row - and it was charged an entry for a line that already
        /// existed. Before the counted tier existed that same bag joined an existing group and cost
        /// nothing, so the tier built to make hoarding free was charging for it. The rule now matches
        /// <see cref="EntryCountLocked"/> exactly: an entry is charged only when a line appears.
        ///
        /// <paramref name="classKey"/> and <paramref name="classDisplayKey"/> are the keys the caller
        /// already computed from <see cref="VaultCollapse.TryDescribeClass"/>, OUTSIDE every lock, or
        /// null when the item is not classifiable. They are passed in rather than recomputed here for
        /// the reason <see cref="HasObviousHeadroom"/> spells out at length: TryDescribeClass takes the
        /// item's own BiotaDatabaseLock, which this store must never hold stateLock across, and calling
        /// it twice for one deposit would also double the most expensive thing on the path.
        /// </summary>
        private bool AddsEntryLocked(bool pristine, WorldObject item, string classKey, string classDisplayKey)
        {
            if (pristine)
                return !ledger.TryGetValue(item.WeenieClassId, out var held) || held <= 0;

            if (classKey != null)
            {
                // The class row itself already exists: the deposit increments a count on a line that is
                // already drawn, exactly as before.
                if (classes.ContainsKey(classKey))
                    return false;

                // A NEW class row, but not necessarily a new LINE. A null display key can only mean
                // the caller could not derive one, and the conservative answer there is "yes, it adds
                // an entry" - it can refuse a deposit that would have been free, never admit one past
                // the cap.
                return classDisplayKey == null || !ClassDisplayGroupIndexLocked().ContainsKey(classDisplayKey);
            }

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

        /// <summary>
        /// True when one more entry provably fits, whether or not the prospective item would consume
        /// one. This is a COST gate, not a second capacity rule: it lets a caller skip
        /// <see cref="WouldAddEntry"/> entirely in the case where that call's answer cannot change the
        /// outcome.
        ///
        /// The reason it exists. <see cref="WouldAddEntry"/> reaches IVaultWorldSource.IsPristine,
        /// which constructs a reference weenie, computes its object description and destroys it -
        /// measurably the most expensive thing on the deposit path, and the sell pre-flight was paying
        /// it a SECOND time per item purely to answer a cap question. When the store is below its cap
        /// the cap cannot refuse, so the answer is not needed. When it is AT the cap it is, and the
        /// caller falls through to the full check.
        ///
        /// It is NOT a cache of IsPristine's result, and must never become one. Caching that answer
        /// across the pre-flight/deposit gap is a TOCTOU over a decision whose false-true collapses a
        /// non-pristine item into a ledger row and destroys the player's item. Skipping the call is
        /// safe; remembering what it said is not.
        ///
        /// False when the store is not READY, matching <see cref="HasRoomFor"/>: an unloaded store
        /// cannot prove it has room, and the conservative answer sends the caller to the full check.
        /// </summary>
        internal bool HasObviousHeadroom()
        {
            lock (stateLock)
            {
                if (!CheckReadyLocked(out _))
                    return false;

                // Exactly TryDeposit's own arithmetic for a deposit that adds one entry, so a caller
                // cleared here cannot be refused there for capacity.
                return HasRoomForLocked(1);
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
        /// THE QUOTIENT ARITHMETIC LIVES IN <see cref="VaultCollapse.WorkmanshipBucket"/> AND NOWHERE
        /// ELSE. It used to be written out here, which made it unreachable for a counted class row -
        /// a class row has no WorldObject - and so the class tier grew a second, FINER notion of which
        /// items are interchangeable. That divergence is the whole 2026-09-25 entry-count defect. Read
        /// that helper's remarks before touching anything here: they are the only record of why the
        /// recovery branch exists, why the cast is guarded, and why WorldObject.Workmanship must never
        /// be read from a vault read path.
        ///
        /// GetProperty, never the ItemWorkmanship/Workmanship properties: plain dictionary reads with
        /// no setter behind them. WorldObject.Workmanship in particular is not a getter - it rewrites
        /// ItemWorkmanship in place (WorldObject_Properties.cs:1587-1598) - and a read of the vault does
        /// not write to a stored item.
        /// </summary>
        private static (uint Wcid, int Structure, int Workmanship) BucketKey(WorldObject item)
        {
            var structureKey = item.Structure.HasValue ? item.Structure.Value : -1;

            var workmanship = VaultCollapse.WorkmanshipBucket(item.GetProperty(PropertyInt.ItemWorkmanship),
                                                              item.GetProperty(PropertyInt.NumItemsInMaterial),
                                                              item.Structure);

            return (item.WeenieClassId, structureKey, workmanship);
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

            groupingRebuilds++;

            var groups = BuildGroupingLocked(out var buckets);

            groupingMemoVersion = stamp;
            groupingMemoVaultCount = vaultCount;
            groupingMemoItemCount = itemCount;
            groupingMemo = groups;
            groupingBucketsMemo = buckets;

            return groups;
        }

        /// <summary>
        /// The grouping build itself, from nothing, touching no memo field. Split out from
        /// <see cref="GroupedStoredItemsLocked"/> so that the differential test
        /// (VaultDepositGroupingOracleTests) can compute the from-scratch answer to compare
        /// <see cref="AddStoredItemToGroupingLocked"/>'s incremental one against WITHOUT disturbing the
        /// live memo - and so that the oracle it compares against is this exact code rather than a
        /// second copy of it in a test, which would be free to drift and then agree with a bug.
        ///
        /// Caller holds <see cref="stateLock"/>.
        /// </summary>
        private List<List<WorldObject>> BuildGroupingLocked(out Dictionary<(uint, int, int), List<List<WorldObject>>> buckets)
        {
            var groups = new List<List<WorldObject>>();

            // Bucket key -> the groups already open in that bucket. Several can be open at once: two
            // bags can share a bucket and still differ somewhere the diff refuses to tolerate.
            buckets = new Dictionary<(uint, int, int), List<List<WorldObject>>>();

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

            return groups;
        }

        /// <summary>
        /// Test seam: the grouping AS THE MEMO CURRENTLY HOLDS IT, projected to guids so a test can
        /// compare it without holding references to live objects. Serves the memo, so after a run of
        /// deposits this is the incrementally maintained answer.
        /// </summary>
        internal List<List<uint>> GroupingSnapshotForTest()
        {
            lock (stateLock)
                return ProjectGroups(GroupedStoredItemsLocked());
        }

        /// <summary>
        /// Test seam: the grouping REBUILT FROM SCRATCH, leaving the memo exactly as it was, which is
        /// what makes it usable as an oracle at every step of a deposit sequence rather than only at the
        /// end. Pairs with <see cref="GroupingSnapshotForTest"/>; the two must always be equal.
        /// </summary>
        internal List<List<uint>> GroupingOracleForTest()
        {
            lock (stateLock)
                return ProjectGroups(BuildGroupingLocked(out _));
        }

        private static List<List<uint>> ProjectGroups(List<List<WorldObject>> groups)
        {
            var projected = new List<List<uint>>(groups.Count);

            foreach (var group in groups)
            {
                var members = new List<uint>(group.Count);

                foreach (var item in group)
                    members.Add(item.Guid.Full);

                projected.Add(members);
            }

            return projected;
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
            groupingBucketsMemo = null;
            groupingMemoVersion = -1;
            groupingMemoVaultCount = -1;
            groupingMemoItemCount = -1;

            // The fold's "nothing left to try" flag is dropped HERE rather than at the handful of sites
            // that add a stored biota, and deliberately: this method is already called by every locked
            // section that mutates vaults or their inventories, so piggybacking on it means no future
            // author has to remember a second rule. The cost of clearing it too eagerly - a removal
            // clears it as well as an insertion - is one fold pass that finds nothing and sets it
            // again, which does no database work at all.
            foldExhausted = false;
        }

        /// <summary>
        /// Records that <paramref name="item"/> has just been added to one of this account's vaults,
        /// MAINTAINING the grouping memo in place instead of dropping it. Called from inside the same
        /// `lock (stateLock)` block that added the item, and it is the deposit path's replacement for
        /// <see cref="InvalidateGroupingLocked"/> - every OTHER mutation site still invalidates, because
        /// only an insertion can be maintained cheaply and a removal or a re-parent cannot.
        ///
        /// WHY THIS IS EXACT AND NOT AN APPROXIMATION. A rebuild partitions the stored items by (bucket
        /// key, groupability), and neither half depends on the order items are visited in:
        ///
        /// - <see cref="BucketKey"/> is a pure function of the one item.
        /// - VaultCollapse.AreGroupable is "these two biotas differ on nothing outside
        ///   GroupableIntKeys" (see its own remarks), which is equality on a projection and therefore an
        ///   EQUIVALENCE relation. So the groups inside one bucket are its equivalence classes, a new
        ///   item belongs to at most one of them, and no arrival can merge two existing classes or move
        ///   an item out of the class it is already in.
        ///
        /// That is what makes an insertion maintainable: adding an item cannot change any OTHER item's
        /// group. What it can change is ORDER, and that is maintained explicitly rather than assumed -
        /// the item is inserted at its own position in
        /// <see cref="EnumerateStoredItemsLocked"/> order inside its group, and if it lands ahead of the
        /// group's current representative the group is repositioned, because
        /// <see cref="GroupedStoredItemsLocked"/> emits each group at its representative's position.
        ///
        /// THE TRANSITIVITY ARGUMENT IS ALSO CHECKED AT RUNTIME rather than only relied on. If more than
        /// one open group in the bucket accepts the item, the relation is not an equivalence relation
        /// after all, this method cannot know which group a rebuild would have chosen, and it falls back
        /// to a full invalidation - correct, just slower. VaultDepositGroupingOracleTests is the
        /// differential test that this fallback never fires for real data.
        /// </summary>
        private void AddStoredItemToGroupingLocked(WorldObject item)
        {
            // Collocated with the mutation and INSIDE stateLock, which is the difference from every
            // other mutation site and is required rather than tidier. The memo is keyed on this stamp,
            // so a bump left until after the lock (where this path's used to be) would leave the
            // freshly maintained memo carrying the PREVIOUS version and the next reader would rebuild
            // it - the memo would be correct and useless. See groupingMemo's remarks, which describe
            // the un-collocated arrangement this replaces on the deposit path.
            var stamp = Interlocked.Increment(ref version);

            // Same reason InvalidateGroupingLocked clears it: the stored set grew, so a previous
            // "nothing left to fold" conclusion no longer holds.
            foldExhausted = false;

            if (groupingMemo == null || groupingBucketsMemo == null)
            {
                // Nothing built yet, so there is nothing to maintain and the next read builds fresh.
                // Invalidating rather than returning keeps the pair and its stamps consistent even if
                // only one half was somehow present.
                InvalidateGroupingLocked();
                return;
            }

            var vaultIndex = VaultIndexLocked();

            if (!TryStoredItemOrdinal(item, vaultIndex, out var ordinal))
            {
                // The item is not in any of this account's vaults, so no position can be derived for it.
                // Unreachable from DepositToVault, which has just added it to one; defensive only.
                log.Warn($"[VAULT] account {AccountId}: could not place 0x{item.Guid.Full:X8} ({item.Name}) in the grouping memo - it is in no known vault. Rebuilding instead.");

                InvalidateGroupingLocked();
                return;
            }

            if (!IsGroupCandidate(item))
            {
                // Not groupable at all, so it is its own group wherever it sits - the same branch the
                // rebuild takes for a non-candidate.
                InsertGroupInOrderLocked(new List<WorldObject> { item }, vaultIndex);

                StampGroupingLocked(stamp);
                return;
            }

            var bucketKey = BucketKey(item);

            if (!groupingBucketsMemo.TryGetValue(bucketKey, out var open))
            {
                open = new List<List<WorldObject>>();
                groupingBucketsMemo[bucketKey] = open;
            }

            // Every open group is examined rather than stopping at the first match, which is what makes
            // the equivalence-relation assumption checkable instead of load bearing and silent.
            List<WorldObject> target = null;
            var matches = 0;

            foreach (var group in open)
            {
                // AreGroupable takes no lock and reaches no database, so it is safe under stateLock -
                // the same reasoning the rebuild states at its own call.
                if (!VaultCollapse.AreGroupable(item.Biota, group[0].Biota))
                    continue;

                matches++;

                if (target == null)
                    target = group;
            }

            if (matches > 1)
            {
                log.Warn($"[VAULT] account {AccountId}: 0x{item.Guid.Full:X8} ({item.Name}) is groupable with {matches} open groups in one bucket, so VaultCollapse.AreGroupable is not transitive and the group a rebuild would choose cannot be inferred here. Rebuilding instead.");

                InvalidateGroupingLocked();
                return;
            }

            if (target == null)
            {
                var opened = new List<WorldObject> { item };

                // The bucket's open list is deliberately NOT kept in any order. A rebuild takes the
                // first groupable group it finds, and with at most one ever groupable, first and only
                // are the same group - so order cannot change which one is chosen.
                open.Add(opened);

                InsertGroupInOrderLocked(opened, vaultIndex);

                StampGroupingLocked(stamp);
                return;
            }

            var at = MemberInsertIndexLocked(target, ordinal, vaultIndex);

            target.Insert(at, item);

            // The new item is the group's representative now, and a group is emitted at its
            // representative's position, so the group has to move to where the new one sits.
            //
            // THIS IS THE ONE BRANCH HERE THAT IS NOT O(1), and the bound is documented rather than
            // fixed: Remove is a reference-equality IndexOf plus a RemoveAt shift, so it is O(g) in the
            // number of GROUPS, which trends to V for a vault of ungroupable items. Measured on the
            // reposition grid in VaultDepositScalingProbeTests it is a pointer memmove worth a small
            // fraction of one AreGroupable diff at V=2000, so it is left alone deliberately - see the
            // bound recorded in groupingMemo's own remarks. The branch is also self-limiting: a
            // deposit appends at the end of its vault, so once the first arrival has moved the group
            // into the earliest vault, later arrivals sort AFTER it and take the O(1) path.
            if (at == 0)
            {
                groupRepositions++;

                groupingMemo.Remove(target);
                InsertGroupInOrderLocked(target, vaultIndex);
            }

            StampGroupingLocked(stamp);
        }

        /// <summary>
        /// Re-stamps the grouping memo as current for <paramref name="stamp"/>. The two counts are read
        /// FRESH rather than adjusted by one, so the memo's own safety net (see
        /// <see cref="groupingMemo"/> defence 2) still compares against live state rather than against
        /// arithmetic this method did.
        /// </summary>
        private void StampGroupingLocked(long stamp)
        {
            groupingMemoVersion = stamp;
            groupingMemoVaultCount = vaults.Count;
            groupingMemoItemCount = StoredItemCountLocked();

            // WHAT ACTUALLY MAKES THIS SAFE is not the guard below, and a future edit should not read it
            // as though it were. Both writers to `classes` drop the class memo OUTRIGHT as their first
            // act - LoadClassRowsLocked at its own InvalidateClassGroupingLocked call (:2034), and
            // ApplyClassCountLocked unconditionally ahead of ALL THREE of its arms (:3473): the remove,
            // the IN-PLACE Count/TotalValue update, and the insert. The in-place arm is the one worth
            // naming, because it mutates a row the memo already holds a reference to rather than
            // replacing it, so nothing downstream could notice it by identity. So a carried-forward
            // stamp can never outlive a real class change whatever this method does; the row-count test
            // below is belt to that braces, not the mechanism.
            //
            // The CLASS display memo is stamped on the same version counter, so the bump above would
            // otherwise make a deposit of a stored biota throw away a class grouping it provably did not
            // touch - `classes` is written only by LoadClassRowsLocked and ApplyClassCountLocked, and
            // this path is neither. Carrying it forward is what keeps a stored-item deposit from paying
            // an O(class rows) rebuild it has no reason to pay. Only ever carried forward when the memo
            // is actually present and was valid a moment ago; a dropped one stays dropped.
            if (classGroupMemo != null && classGroupIndexMemo != null && classGroupMemoRowCount == classes.Count)
                classGroupMemoVersion = stamp;
        }

        /// <summary>
        /// Vault container guid -> its index in <see cref="vaults"/>, which is the first component of
        /// <see cref="EnumerateStoredItemsLocked"/>'s order. Caller holds <see cref="stateLock"/>.
        /// </summary>
        private Dictionary<uint, int> VaultIndexLocked()
        {
            var index = new Dictionary<uint, int>(vaults.Count);

            for (var i = 0; i < vaults.Count; i++)
                index[vaults[i].Guid.Full] = i;

            return index;
        }

        /// <summary>
        /// One stored item's position in <see cref="EnumerateStoredItemsLocked"/> order, as the triple
        /// that order sorts on: vault index, then PlacementPosition, then guid. FALSE when the item is
        /// not in one of this account's vaults, which is the only case no position exists for.
        ///
        /// The vault is resolved through ContainerId because that is what
        /// Container.TryAddToInventory sets it to (Container.cs:562), so it is the item's own record of
        /// which vault holds it and needs no scan of every inventory.
        /// </summary>
        private static bool TryStoredItemOrdinal(WorldObject item, Dictionary<uint, int> vaultIndex, out (int Vault, int Placement, uint Guid) ordinal)
        {
            ordinal = default;

            var containerId = item.ContainerId;

            if (containerId == null || !vaultIndex.TryGetValue(containerId.Value, out var vault))
                return false;

            ordinal = (vault, item.PlacementPosition ?? 0, item.Guid.Full);
            return true;
        }

        private static int CompareStoredOrdinal((int Vault, int Placement, uint Guid) a, (int Vault, int Placement, uint Guid) b)
        {
            if (a.Vault != b.Vault)
                return a.Vault < b.Vault ? -1 : 1;

            if (a.Placement != b.Placement)
                return a.Placement < b.Placement ? -1 : 1;

            if (a.Guid != b.Guid)
                return a.Guid < b.Guid ? -1 : 1;

            return 0;
        }

        /// <summary>
        /// Compares <paramref name="ordinal"/> against the position of <paramref name="other"/>, treating
        /// an item whose vault cannot be resolved as sorting AFTER everything. That convention is what
        /// keeps both of the searches below monotonic, and so binary-searchable: a list ordered by
        /// position with any unplaceable entries at the end is still sorted under this comparison.
        ///
        /// Unplaceable is unreachable for anything the memo holds - every member of a group is an item
        /// inside one of this account's vault containers, which is what gives it a position at all - so
        /// this is the defensive arm rather than a case with behaviour worth relying on.
        /// </summary>
        private static int CompareOrdinalToItem((int Vault, int Placement, uint Guid) ordinal, WorldObject other, Dictionary<uint, int> vaultIndex)
        {
            if (!TryStoredItemOrdinal(other, vaultIndex, out var position))
                return -1;

            return CompareStoredOrdinal(ordinal, position);
        }

        /// <summary>
        /// Where <paramref name="ordinal"/> belongs among <paramref name="group"/>'s members, which a
        /// rebuild leaves in <see cref="EnumerateStoredItemsLocked"/> order because that is the order it
        /// adds them in.
        ///
        /// The APPEND case is answered in one comparison and is the case that matters: a deposit lands at
        /// the end of its vault, so unless an earlier vault has a hole the new item sorts after every
        /// member already there. Everything else binary-searches. A linear scan here was the residual
        /// O(V) term in the first version of this fix - for a vault whose items are all one group, "walk
        /// the members" IS "walk the vault".
        /// </summary>
        private static int MemberInsertIndexLocked(List<WorldObject> group, (int Vault, int Placement, uint Guid) ordinal, Dictionary<uint, int> vaultIndex)
        {
            if (group.Count == 0 || CompareOrdinalToItem(ordinal, group[group.Count - 1], vaultIndex) >= 0)
                return group.Count;

            var lo = 0;
            var hi = group.Count;

            while (lo < hi)
            {
                var mid = lo + ((hi - lo) / 2);

                if (CompareOrdinalToItem(ordinal, group[mid], vaultIndex) < 0)
                    hi = mid;
                else
                    lo = mid + 1;
            }

            return lo;
        }

        /// <summary>
        /// Inserts <paramref name="group"/> into <see cref="groupingMemo"/> at its REPRESENTATIVE's
        /// position, which is the ordering <see cref="GroupedStoredItemsLocked"/> documents and produces.
        /// Caller holds <see cref="stateLock"/>.
        ///
        /// Same two-tier search as <see cref="MemberInsertIndexLocked"/> and for the same reason: an
        /// appending deposit answers in one comparison, and a vault of ungroupable items has one group
        /// per item, so a linear scan of the groups would again be a scan of the vault.
        /// </summary>
        private void InsertGroupInOrderLocked(List<WorldObject> group, Dictionary<uint, int> vaultIndex)
        {
            if (!TryStoredItemOrdinal(group[0], vaultIndex, out var ordinal))
            {
                groupingMemo.Add(group);
                return;
            }

            if (groupingMemo.Count == 0
                || CompareOrdinalToItem(ordinal, groupingMemo[groupingMemo.Count - 1][0], vaultIndex) >= 0)
            {
                groupingMemo.Add(group);
                return;
            }

            var lo = 0;
            var hi = groupingMemo.Count;

            while (lo < hi)
            {
                var mid = lo + ((hi - lo) / 2);

                if (CompareOrdinalToItem(ordinal, groupingMemo[mid][0], vaultIndex) < 0)
                    hi = mid;
                else
                    lo = mid + 1;
            }

            groupingMemo.Insert(lo, group);
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

        #region Class row display grouping (Docs/MuleVendor/CLASS-TIER-ENTRY-COUNT-FIX.md)

        /// <summary>
        /// The class rows bucketed into the lines the panel actually draws, memoized on
        /// <see cref="version"/> plus the row count. Caller holds <see cref="stateLock"/>.
        ///
        /// THE PARTITION IS COARSER THAN <see cref="classes"/> AND THAT IS THE POINT. A class key
        /// carries the raw (ItemWorkmanship, NumItemsInMaterial) pair so a withdraw can hand back
        /// exactly what was deposited; the panel buckets their QUOTIENT, because that is what the
        /// player sees. Several class rows therefore legitimately share one line, and this is the one
        /// place that correspondence is computed.
        ///
        /// Groups come out in FIRST-MEMBER class-key order and their members in class-key ascending
        /// order, matching the DAO's own ORDER BY and the order EnumerateEntriesLocked used to yield
        /// single rows in, so the panel stays stable across reads.
        ///
        /// NOTHING HERE MUTATES A ROW. It is a read-time projection exactly like
        /// <see cref="GroupedStoredItemsLocked"/>: no row is merged, rewritten or deleted, and every
        /// member keeps its own canonical form so a withdraw is still exact.
        /// </summary>
        private List<List<VaultClassRow>> ClassDisplayGroupsLocked()
        {
            EnsureClassDisplayGroupingLocked();

            return classGroupMemo;
        }

        /// <summary>
        /// The display key -> group index map behind <see cref="ClassDisplayGroupsLocked"/>, so the cap
        /// check is a hash lookup rather than a walk. Caller holds <see cref="stateLock"/>.
        /// </summary>
        private Dictionary<string, int> ClassDisplayGroupIndexLocked()
        {
            EnsureClassDisplayGroupingLocked();

            return classGroupIndexMemo;
        }

        private void EnsureClassDisplayGroupingLocked()
        {
            // Both read BEFORE the build, so a stamp can only ever be one step behind the contents it
            // labels, never ahead of them - the same discipline GroupedStoredItemsLocked keeps.
            var stamp = Volatile.Read(ref version);
            var rowCount = classes.Count;

            if (classGroupMemo != null && classGroupIndexMemo != null && classGroupIdMemo != null
                && classGroupMemoVersion == stamp
                && classGroupMemoRowCount == rowCount)
                return;

            classGroupingRebuilds++;

            var groups = new List<List<VaultClassRow>>();
            var ids = new List<string>();
            var index = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var row in classes.Values.OrderBy(r => r.ClassKey, StringComparer.Ordinal))
            {
                var key = ClassDisplayKeyFor(row);

                if (index.TryGetValue(key, out var at))
                {
                    groups[at].Add(row);
                    continue;
                }

                index[key] = groups.Count;
                groups.Add(new List<VaultClassRow> { row });
                ids.Add(VaultCollapse.ClassDisplayId(key));
            }

            classGroupMemoVersion = stamp;
            classGroupMemoRowCount = rowCount;
            classGroupIndexMemo = index;
            classGroupIdMemo = ids;
            classGroupMemo = groups;
        }

        /// <summary>
        /// One row's display key.
        ///
        /// A row whose payload this build could not read gets a group OF ITS OWN, keyed on its class
        /// key. That is the conservative direction - one entry for one drawn row - and it cannot
        /// collide with a real display key, which always opens with the key format's own stamp.
        /// Unreachable today, because <see cref="LoadClassRowsLocked"/> refuses a row whose canonical
        /// form will not parse rather than admitting it with a null payload; it is here so that a
        /// future producer which does not cannot silently merge unreadable rows into one line.
        /// </summary>
        private static string ClassDisplayKeyFor(VaultClassRow row)
        {
            if (row.Overrides == null)
                return "unparsed|" + row.ClassKey;

            return VaultCollapse.ClassDisplayGroupKey(row.Wcid, row.Overrides);
        }

        /// <summary>
        /// Drops the class display grouping memo. MUST be called from inside the same
        /// `lock (stateLock)` block that mutates <see cref="classes"/>, never after it, for exactly the
        /// reason <see cref="InvalidateGroupingLocked"/> gives: the defect this guards against is a
        /// reader acquiring stateLock in the gap between a mutation and its bookkeeping.
        ///
        /// There are only two writers to <see cref="classes"/>, <see cref="LoadClassRowsLocked"/> and
        /// <see cref="ApplyClassCountLocked"/>, and both call this.
        /// </summary>
        private void InvalidateClassGroupingLocked()
        {
            classGroupMemo = null;
            classGroupIndexMemo = null;
            classGroupIdMemo = null;
            classGroupMemoVersion = -1;
            classGroupMemoRowCount = -1;
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

        /// <summary>
        /// True when this store could serve <see cref="GetEntries(int, int)"/> RIGHT NOW with no shard
        /// read: the index and ledger are in memory, no ledger reload is pending, and every vault's
        /// inventory has finished loading.
        ///
        /// The one reader here that NEVER starts a load, and that is its whole reason to exist.
        /// <see cref="IsLoaded"/> runs <see cref="TryEnsureLoadedLocked"/> synchronously under the
        /// store lock - an index read, a ledger read and one biota read per vault - so asking it about
        /// ten shared mules from one /mule search all would put up to ten cold loads on the world tick.
        /// This answers from state alone; a cold store is warmed later through
        /// AccountVaultManager.RequestWarm, which rate-limits the load.
        /// </summary>
        public bool IsWarm
        {
            get
            {
                lock (stateLock)
                    return IsWarmLocked();
            }
        }

        /// <summary>
        /// <see cref="IsWarm"/> for a caller that already holds <see cref="stateLock"/>.
        ///
        /// Split out rather than reading the property from inside the lock (which C# would allow,
        /// Monitor being reentrant) so that the "never starts a load" guarantee has ONE body. A second
        /// hand-written copy of this condition is exactly how a future edit teaches one of the two
        /// callers to trigger a cold load without anyone noticing.
        ///
        /// Warm is strictly STRONGER than ready: <see cref="CheckReadyLocked"/> is
        /// <see cref="TryEnsureLoadedLocked"/> plus <see cref="AllVaultsLoadedLocked"/>, and a warm
        /// store satisfies TryEnsureLoadedLocked's fast path by definition - indexLoaded is set and
        /// neither reload flag is pending, so it returns true without touching the backend.
        /// </summary>
        private bool IsWarmLocked()
        {
            return indexLoaded && !ledgerReloadPending && !classReloadPending && AllVaultsLoadedLocked();
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
            return CheckReadyLocked(out failReason, false);
        }

        /// <summary>
        /// <see cref="CheckReadyLocked(out string)"/> with the deposit path's one concession. See
        /// <see cref="TryEnsureLoadedLocked(bool)"/> for what <paramref name="tolerateDeferredReload"/>
        /// means and why only <see cref="TryDepositBatch"/> passes true.
        /// </summary>
        private bool CheckReadyLocked(out string failReason, bool tolerateDeferredReload)
        {
            if (!TryEnsureLoadedLocked(tolerateDeferredReload))
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
            return TryEnsureLoadedLocked(false);
        }

        /// <summary>
        /// <see cref="TryEnsureLoadedLocked()"/> with the deposit path's one concession, and the concession
        /// is narrow on purpose.
        ///
        /// <paramref name="tolerateDeferredReload"/> true means: treat a reload-pending flag as satisfied
        /// IF an open <see cref="VaultDepositWindow"/> has marked that table's re-read as deferred to its
        /// close. Every caller that serves a COUNT still passes false and so still gets the unconditional
        /// refresh; only <see cref="TryDepositBatch"/> passes true.
        ///
        /// WHAT IS AND IS NOT TOLERATED, stated exactly, because this comment is the whole written safety
        /// argument for the concession. A flag that was already set when the window OPENED is NOT tolerated,
        /// and neither is one for a table the window never deferred: the window's <c>Deferred*</c> flag is
        /// what gates the tolerance, and it is set only by a phase D that chose to defer. But once the
        /// window has deferred a table, this method cannot tell WHY that table's flag is still set, so a
        /// re-read that is attempted and FAILS while the window is open - a foreign reader's, or the
        /// window's own from a previous call - is tolerated for the rest of the window. That is deliberate
        /// and is pinned by DepositWindow_WhenTheCoalescedReReadFails_TheStoreRefusesRatherThanServingAStaleCount:
        /// the only staleness a window can create is ADDITIVE OMISSION - lines and units this store itself
        /// just credited and has not read back - so the deposit path is never shown a row that does not
        /// exist or a count above the truth. The credit itself is adjudicated by a guarded statement in the
        /// database rather than by any in-memory number, so no staleness here can make a wrong credit.
        ///
        /// THE CAP IS NOT SAFE BY OMISSION ALONE, and that has to be stated rather than assumed. A line this
        /// window created is missing from the re-read table, so it is counted only if something else counts
        /// it: presence is carried on the window (<see cref="VaultDepositWindow.LedgerPresent"/>) for the
        /// DAO's oracle, and the cap's own count is carried as CLAIMS (<see cref="VaultEntryClaims"/>) which
        /// are dropped when their table is re-read and held back while the credits that create their rows
        /// are in flight (<see cref="VaultDepositWindow.ClaimsInFlight"/>). With that hold in place, every
        /// line the window has created is counted at least once and sometimes twice, so the cap errs only
        /// toward refusing a deposit that would have fit. Without it the same line is counted zero times for
        /// the length of a batch and the cap is BREACHED, which is why the hold exists and why the two
        /// clauses belong in the same paragraph. The window's close still re-reads, and if THAT fails the
        /// flag survives the window and every caller is refused again.
        ///
        /// WHY IT IS SAFE FOR THE DEPOSIT PATH SPECIFICALLY. What the deposit path reads out of
        /// <see cref="ledger"/> and <see cref="classes"/> is line PRESENCE, never a count: the entry-cap
        /// rule (<see cref="AddsEntryLocked"/>, <see cref="EntryCountLocked"/>) asks whether a line exists
        /// and how many lines there are, and the DAO's "already present" oracle asks whether a row exists.
        /// Presence for the lines a window has created is carried on the window itself - see
        /// <see cref="VaultEntryClaims"/> and <see cref="VaultDepositWindow.LedgerPresent"/> - so the
        /// deposit path is answering from carried fact rather than from a stale read. The credit itself is
        /// adjudicated by a guarded statement in the database and never by an in-memory number, so no
        /// staleness here can make a wrong credit.
        /// </summary>
        private bool TryEnsureLoadedLocked(bool tolerateDeferredReload)
        {
            if (indexLoaded)
            {
                // The open window, or null for every caller that did not opt in. `tolerateDeferredReload`
                // alone changes nothing: with no window open the two conditions below are exactly the two
                // pending flags, so this method behaves identically for every existing caller.
                var window = tolerateDeferredReload ? depositWindow : null;

                var ledgerStale = ledgerReloadPending && !(window != null && window.DeferredLedgerReload);
                var classStale = classReloadPending && !(window != null && window.DeferredClassReload);

                // Fix round 2, F1. A committed ledger delta whose count could not be read back leaves
                // this store holding a number that is provably wrong for one wcid. Re-read the ledger
                // before serving anything, and REFUSE if that read fails rather than serving the stale
                // count: the vault's whole null-versus-empty discipline rests on never presenting a
                // number this store cannot stand behind, and a wrong count is worse than a refusal
                // because the player acts on it. The reload is only the ledger - the vault index and
                // the containers are untouched by a ledger delta, and rebuilding them would throw away
                // every container's finished async inventory load (R4) for nothing.
                if (!ledgerStale && !classStale)
                    return true;

                // Each pending flag re-reads ONLY its own table. They are separate because a class
                // outcome nobody could read back says nothing about the stack ledger, and re-reading
                // both would let a failure on the healthy table refuse a store over a doubt that never
                // applied to it.
                if (ledgerStale && !TryReloadLedgerLocked())
                    return false;

                if (classStale && !TryReloadClassesLocked())
                    return false;

                return true;
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

            // Same null-versus-empty rule as the two reads above: null is a FAILED read, an empty list
            // is an account that holds no classes, and refusing is the only safe answer to the first.
            var classRows = backend.GetAccountVaultClasses(AccountId);

            if (classRows == null)
            {
                log.Error($"[VAULT] account {AccountId}: could not read the item-class ledger; refusing rather than treating it as an empty ledger.");
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

            LoadClassRowsLocked(classRows);

            indexLoaded = true;
            ledgerReloadPending = false;
            classReloadPending = false;
            return true;
        }

        /// <summary>
        /// Replaces <see cref="classes"/> from a freshly read set of rows, parsing each row's canonical
        /// form exactly once.
        ///
        /// A ROW IS REFUSED RATHER THAN GUESSED AT, and refused rows are LEFT ALONE in the database.
        /// Three things can be wrong with one, and all three mean the same thing - this build cannot
        /// prove what the row's items are, so it must not hand any of them to a player:
        ///
        ///   - the canonical form will not parse (a truncated column, a future format version);
        ///   - the denormalized wcid disagrees with the parsed form;
        ///   - the denormalized band disagrees with the parsed form.
        ///
        /// The last two are why both columns are cross-checked rather than trusted: they exist for
        /// indexing and display, so nothing else would ever notice them drifting, and a row served
        /// under a wcid its payload does not name would materialize the wrong item entirely.
        ///
        /// A refused row is logged and skipped. It keeps its items - nothing is destroyed, nothing is
        /// deleted - and it simply does not appear in the panel until an operator fixes it, which is
        /// the same degradation a row that cannot be read gets everywhere else in this class.
        ///
        /// Caller holds <see cref="stateLock"/>.
        /// </summary>
        private void LoadClassRowsLocked(List<AccountVaultClass> classRows)
        {
            // Everything the fold learned was learned about a set of stored biotas this store is about
            // to replace, so both halves of its memory are dropped. A guid it refused may not even be
            // in this account's vaults any more.
            foldRefused.Clear();
            foldClassHints.Clear();
            foldTargetClassKey = null;
            foldExhausted = false;

            // Inside the same locked section as the mutation, beside it - see
            // InvalidateClassGroupingLocked's remarks for why "after the lock" is not good enough.
            InvalidateClassGroupingLocked();

            classes.Clear();

            foreach (var row in classRows)
            {
                if (row.Count <= 0)
                    continue;

                if (!VaultItemClass.TryParseCanonicalForm(row.CanonicalForm, out var wcid, out var overrides, out var band))
                {
                    log.Error($"[VAULT] account {AccountId}: account_vault_class row {row.Id} (class {row.ClassKey}) has a canonical form this build cannot read. The row is UNTOUCHED and its {row.Count} item(s) are not lost, but it will not appear in the vault until it can be parsed.");
                    continue;
                }

                if (wcid != row.Wcid || band != row.ValueBandPct)
                {
                    log.Error($"[VAULT] account {AccountId}: account_vault_class row {row.Id} (class {row.ClassKey}) disagrees with its own canonical form - the row says wcid {row.Wcid} band {row.ValueBandPct}, the form says wcid {wcid} band {band}. The row is UNTOUCHED and will not appear in the vault; serving it would materialize an item its payload does not describe.");
                    continue;
                }

                classes[row.ClassKey] = new VaultClassRow
                {
                    ClassKey = row.ClassKey,
                    Wcid = wcid,
                    Count = row.Count,
                    TotalValue = row.TotalValue,
                    CanonicalForm = row.CanonicalForm,
                    Overrides = overrides,
                };
            }
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

            // `ledger` is now authoritative for every wcid, so a deposit window's carried state for this
            // table is both redundant and no longer its own to answer from. The window's DEFERRED flag goes
            // with it: whatever it deferred has just been read. Cleared HERE rather than at the window's
            // close, because this reload is not always the window's own - a panel read landing mid-delivery
            // runs it, and so does an EntryCount or GetEntries call from the SAME thread inside the window,
            // because those pass tolerateDeferredReload: false - and after it the window must not keep
            // claiming to own a re-read that has already happened.
            //
            // THE CLAIMS GO TOO, BUT ONLY WHEN NONE IS IN FLIGHT, and each half of that is a correctness
            // rule pulling the opposite way.
            //
            // DROPPING THEM once their rows are readable: a surviving key would merely be redundant, because
            // the reloaded ledger answers the same question, but a surviving Entries is ADDED to the
            // reloaded EntryCountLocked() at every later cap check in this window, so the account is charged
            // twice for the same line and a deposit that fits is refused with the vault-full message -
            // which, on the market path, ClassifyDepositFailure turns into MarketError.VaultFull and a paid
            // delivery into a partial sale.
            //
            // KEEPING THEM while a batch is mid-flight: a claim is recorded in phase A and the row it
            // describes is created in phase B, so between the two this reload cannot see the line either.
            // Dropping the claim there subtracts a line the account really holds from the cap sum and lets
            // the next item past a cap it has already reached - an UNDER-count, which is the one direction
            // the cap must never take. ClaimsInFlight marks exactly that interval. Holding the claims
            // through it costs the double charge described above, for the items of one batch rather than
            // for the rest of the window, and a refusal is survivable where a breach is not.
            //
            // PRESENCE IS CLEARED EITHER WAY. It only steers the DAO's insert-versus-update branch, where
            // being wrong costs an AUTO_INCREMENT id and never a wrong credit, and the table just re-read is
            // the better oracle for it.
            if (depositWindow != null)
            {
                depositWindow.DeferredLedgerReload = false;
                depositWindow.LedgerPresent.Clear();

                if (!depositWindow.ClaimsInFlight)
                    depositWindow.LedgerClaims.Clear();
            }

            // The listed counts may have changed, so any open window is stale.
            Interlocked.Increment(ref version);

            return true;
        }

        /// <summary>
        /// <see cref="TryReloadLedgerLocked"/>'s counterpart for account_vault_class. Same contract:
        /// caller holds <see cref="stateLock"/>, a null read leaves the previous contents and
        /// <see cref="classReloadPending"/> alone, and null is a read FAILURE rather than an empty
        /// ledger.
        /// </summary>
        private bool TryReloadClassesLocked()
        {
            var classRows = backend.GetAccountVaultClasses(AccountId);

            if (classRows == null)
                return false;

            LoadClassRowsLocked(classRows);

            classReloadPending = false;

            // The class table's counterpart of the clear in TryReloadLedgerLocked, for the same reasons -
            // including the claims, whose survival past a re-read is the double-charge described there and
            // whose premature drop, while a batch's credits are still in flight, is the cap breach described
            // there. Each table answers the question for its own claims, so a class reload never touches the
            // ledger's set and the ClaimsInFlight interval is the same one for both.
            if (depositWindow != null)
            {
                depositWindow.DeferredClassReload = false;
                depositWindow.ClassPresent.Clear();

                if (!depositWindow.ClaimsInFlight)
                    depositWindow.ClassClaims.Clear();
            }

            // The listed counts may have changed, so any open window is stale.
            Interlocked.Increment(ref version);

            return true;
        }

        /// <summary>
        /// <see cref="InvalidateLedger"/>'s counterpart for <see cref="classes"/>. Same reasoning
        /// exactly: mark stale, try to refresh immediately from the mutation queue, and if the re-read
        /// also fails leave the flag set so the store refuses rather than serving a count it knows is
        /// wrong.
        /// </summary>
        private void InvalidateClasses(string why)
        {
            lock (stateLock)
            {
                classReloadPending = true;

                if (!indexLoaded)
                    return;     // nothing loaded to be stale; the first load will read the classes anyway

                if (!TryReloadClassesLocked())
                    log.Error($"[VAULT] account {AccountId}: could not re-read the item-class ledger after {why}. The store will refuse reads and mutations until a later read succeeds, rather than serve a count it knows is stale.");
            }
        }

        /// <summary>
        /// Opens a coalescing deposit window over the caller's next several <see cref="TryDeposit"/> calls,
        /// so they share ONE phase-D re-read instead of paying a whole-account read each. See
        /// <see cref="VaultDepositWindow"/> for the contract; dispose it in a finally (a <c>using</c>), and
        /// never hold one across anything but deposits into this store.
        ///
        /// FOR A CALLER THAT MUST KEEP ITS PER-ITEM LOOP. A caller free to hand this store its whole item
        /// list should call <see cref="TryDepositBatch"/> instead and get the batched STATEMENTS as well;
        /// this exists for the one caller that cannot, because its per-item commit classification decides
        /// who owns each item.
        ///
        /// Nesting hands back a no-op, so an inner caller cannot close an outer caller's window. Nothing
        /// nests today; the guard is here because closing someone else's window would run their re-read
        /// early and silently restore the per-item cost they opened it to avoid.
        /// </summary>
        internal IDisposable BeginDepositWindow()
        {
            AssertOnMutationQueue(nameof(BeginDepositWindow));

            lock (stateLock)
            {
                if (depositWindow != null)
                    return VaultNoOpDisposable.Instance;

                depositWindow = new VaultDepositWindow(CloseDepositWindow);

                return depositWindow;
            }
        }

        /// <summary>
        /// Runs the window's one coalesced re-read and closes it.
        ///
        /// NEVER THROWS, and that is load-bearing rather than defensive. This runs from
        /// <see cref="VaultDepositWindow.Dispose"/>, which the market delivery path reaches through a
        /// <c>using</c> around the loop that decides, per item, whether it stays with the buyer or goes back
        /// to the seller. An exception out of a dispose there would replace that decision with a throw from
        /// somewhere that has no opinion about it. A re-read that fails or throws leaves the reload-pending
        /// flags SET, which is guarantee 4's own failure mode: the store refuses reads and mutations until a
        /// later read succeeds. THE DEPOSITS STAND either way - their credits committed and their items are
        /// gone - and this failure must never be read as an undo.
        /// </summary>
        private void CloseDepositWindow(VaultDepositWindow window)
        {
            // THE WHOLE BODY IS INSIDE THE GUARD, including the instrumentation and the failure log. An
            // earlier shape caught only the re-read: the profiler calls sat outside it and the catch's own
            // log.Error sat in the catch, so a throw from any of those three - while the `using` was already
            // unwinding an exception out of the delivery loop - would have REPLACED that exception with one
            // from a routine that has no opinion about who owns which item. That is exactly the failure the
            // "never throws" contract above exists to prevent, so the contract is now enforced by structure
            // rather than by the reader noticing which statements the try covered.
            try
            {
                CloseDepositWindowCore(window);
            }
            catch (Exception ex)
            {
                LogWindowCloseFailure(ex);
            }
        }

        /// <summary>
        /// <see cref="CloseDepositWindow"/>'s actual work. May throw; its caller is the guard.
        /// </summary>
        private void CloseDepositWindowCore(VaultDepositWindow window)
        {
            // Charged to VaultDepositPhase.Reload even though it runs outside every deposit scope: it IS
            // the deposits' re-read, only hoisted, and leaving it uncharged would make vd_reload_n read 0
            // for exactly the path this window was built for. See BeginDeferredReload.
            var reloadScope = VaultDepositPhaseProfile.BeginDeferredReload();

            try
            {
                lock (stateLock)
                {
                    if (!ReferenceEquals(depositWindow, window))
                        return;

                    depositWindow = null;

                    if (window.DeferredLedgerReload && ledgerReloadPending && !TryReloadLedgerLocked())
                        log.Error($"[VAULT] account {AccountId}: could not re-read the stack ledger at the close of a coalescing deposit window. The store will refuse reads and mutations until a later read succeeds, rather than serve a count it knows is stale. THE DEPOSITS STAND - their credits committed and the items are gone - and this failure must never be read as an undo.");

                    if (window.DeferredClassReload && classReloadPending && !TryReloadClassesLocked())
                        log.Error($"[VAULT] account {AccountId}: could not re-read the item-class ledger at the close of a coalescing deposit window. The store will refuse reads and mutations until a later read succeeds, rather than serve a count it knows is stale. THE DEPOSITS STAND - their credits committed and the items are gone - and this failure must never be read as an undo.");
                }
            }
            finally
            {
                VaultDepositPhaseProfile.EndDeferredReload(reloadScope);
            }
        }

        /// <summary>
        /// Reports a window close that threw, and CANNOT itself throw. The bare catch is deliberate and is
        /// the last link in the chain: an appender that is misconfigured, disposed at shutdown or out of
        /// disk must not be the thing that escapes a dispose and replaces a delivery's real exception. A
        /// swallowed logging failure loses a diagnostic; a thrown one loses the custody decision.
        /// </summary>
        private void LogWindowCloseFailure(Exception ex)
        {
            try
            {
                log.Error($"[VAULT] account {AccountId}: the coalesced re-read at the close of a deposit window THREW. The reload-pending flags are left set, so the store refuses reads and mutations until a later read succeeds. THE DEPOSITS STAND - their credits committed and the items are gone - and this failure must never be read as an undo. {ex}");
            }
            catch
            {
                // Deliberately empty; see this method's remarks.
            }
        }

        #endregion

        #region Mutation queue (R3)

        /// <summary>
        /// True while the calling thread is executing work off this store's mutation queue.
        /// </summary>
        public bool IsOnMutationQueue => mutationThreadId == Environment.CurrentManagedThreadId;

        /// <summary>
        /// Runs on the mutation queue before <see cref="TryWithdraw"/> moves anything, with (accountId,
        /// itemGuid, wcid, amount, classDisplayId); itemGuid is null for a ledger row AND for a class
        /// row, and classDisplayId (<see cref="VaultEntry.ClassDisplayId"/>) is non-null for a class row
        /// only. The class id is what lets a subscriber tell a class withdraw from a ledger withdraw of
        /// the same wcid; without it the two are indistinguishable here. Static, because nothing here
        /// may name a type outside this namespace. A subscriber MUST NOT throw or call back into this
        /// store.
        /// </summary>
        public static Action<uint, uint?, uint, int, string> PreWithdrawHook;

        /// <summary>
        /// Answers whether (accountId, itemGuid, wcid, classDisplayId) currently carries an ACTIVE
        /// market listing; itemGuid is null for a ledger row and a class row, and classDisplayId is
        /// non-null for a class row only. Set by MarketManager on startup and cleared on shutdown,
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
        public static Func<uint, uint?, uint, string, bool> IsListedHook;

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
                        // BREAK, not return: the queue being empty is what ends a drain, and the
                        // coalesced container save below is what an ended drain owes the shard.
                        if (queue.Count == 0)
                            break;

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

                // Still inside drainLock, and outside queueLock. One save per touched vault container
                // for the whole drain, instead of one per deposited item - see dirtyVaults for why
                // that cannot lose an item and why the same-id saves were worth collapsing.
                FlushDirtyVaultSaves();
            }
        }

        /// <summary>
        /// Saves every vault container a deposit touched since the last flush, exactly once each.
        ///
        /// Called from <see cref="Drain"/> with <see cref="drainLock"/> held, which is the same
        /// exclusion the queued work ran under, so the set cannot grow underneath this.
        ///
        /// Each save is guarded individually: SaveBiota reaches SerializedShardDatabase, whose
        /// BlockingCollection.Add throws once CompleteAdding() has run at shutdown, and a throw here
        /// would escape Drain past the per-item catch above and into whichever caller happened to be
        /// the one that emptied the queue.
        /// </summary>
        private void FlushDirtyVaultSaves()
        {
            List<Container> pending;

            lock (stateLock)
            {
                if (dirtyVaults.Count == 0)
                    return;

                pending = new List<Container>(dirtyVaults);
                dirtyVaults.Clear();
            }

            foreach (var vault in pending)
            {
                try
                {
                    world.SaveBiota(vault);
                }
                catch (Exception ex)
                {
                    log.Error($"[VAULT] account {AccountId}: saving vault container 0x{vault.Guid.Full:X8} after a drain threw: {ex.GetFullMessage()}. Its items are unaffected - their own rows record where they are - but the container's aggregate properties are stale on disk until the next deposit into it.");
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
        /// equivalent - followed by the non-zero ledger rows, followed by the counted class rows.
        ///
        /// A one-member group is emitted as an ORDINARY item entry rather than a group of one, which is
        /// what keeps every existing behaviour of a lone stored item exactly as it was: its Count is
        /// still its own StackSize, and a partial withdraw of it is still refused.
        ///
        /// CLASS ROWS ARE YIELDED, and that is not optional: <see cref="EntryCountLocked"/> counts them
        /// against the cap, so a store that counted them and did not draw them would refuse deposits
        /// over rows the player cannot see. Ordered by class key so the panel's row order is stable
        /// across reads, matching the DAO's own ORDER BY.
        ///
        /// ONE ENTRY PER DISPLAY GROUP, not per class row. Several class rows can differ only on the
        /// raw (ItemWorkmanship, NumItemsInMaterial) pair while rendering the same quotient, in which
        /// case the panel drew them as one line and the cap counted them as several - the 2026-09-25
        /// defect. Both halves now read <see cref="ClassDisplayGroupsLocked"/>, so they cannot
        /// disagree; the rows themselves are untouched and travel on the entry's ClassMembers list so
        /// a withdraw is still exact.
        /// </summary>
        private IEnumerable<VaultEntry> EnumerateEntriesLocked()
        {
            foreach (var group in GroupedStoredItemsLocked())
                yield return group.Count > 1 ? VaultEntry.ForGroup(group) : VaultEntry.ForItem(group[0]);

            foreach (var kvp in ledger.Where(k => k.Value > 0).OrderBy(k => k.Key))
                yield return VaultEntry.ForLedger(kvp.Key, kvp.Value);

            var classGroups = ClassDisplayGroupsLocked();
            var classIds = classGroupIdMemo;

            for (var g = 0; g < classGroups.Count; g++)
            {
                var group = classGroups[g];
                var members = new List<VaultClassMember>(group.Count);

                foreach (var row in group)
                    members.Add(new VaultClassMember(row.ClassKey, row.CanonicalForm, row.Count, row.TotalValue));

                // The representative's wcid and name. Every member of a display group shares the wcid
                // by construction (it is part of the display key) and shares the Name for the same
                // reason, so neither is a choice between differing values. The display id comes from
                // the same memo build as the grouping itself, so it can never describe another line.
                yield return VaultEntry.ForClass(group[0].Wcid, members, group[0].DisplayName, classIds[g]);
            }
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
        /// Stores ONE item. DELEGATES to <see cref="TryDepositBatch"/> with a one-element list, and the
        /// delegation is the point rather than a convenience: it leaves exactly ONE implementation of the
        /// deposit/withdraw asymmetry and of the ledger-first ordering, so the single-item and batched
        /// paths cannot drift apart. The ORDER of those steps is the R5 mitigation and is not stylistic -
        /// ledger first, destroy second, because a dupe is recoverable from the audit log and a vanished
        /// item is not.
        ///
        /// The caller must already have detached the item from the actor (the vendor sell path does
        /// this before it gets here). An item still parented to a player would end up in two
        /// containers at once, so it is refused rather than accepted.
        ///
        /// Nothing here but the per-phase timing scope and the unwrap of the single outcome. The scope is
        /// closed in a finally so a throw (the CLASS/LEDGER STATE UNKNOWN paths are real) cannot leave the
        /// instrument armed for whatever the world thread runs next. Inert (two static bool reads and a 0
        /// return) while world_tick_slow_log_enabled is off, its shipped default.
        /// </summary>
        internal bool TryDeposit(WorldObject item, VaultActor actor, out string failReason)
        {
            // FIRST, ahead of even the queue assertion and of the timing scope: a stale Committed left by
            // the previous deposit would otherwise classify this one as landed if anything below throws
            // before the next write. See LastDepositCommit. TryDepositBatch repeats it as its own first
            // statement, for a caller that reaches the batch directly.
            depositCommit = VaultDepositCommit.None;

            var depositScope = VaultDepositPhaseProfile.BeginDeposit();

            try
            {
                var ok = TryDepositBatch(new[] { item }, actor, out var outcomes);

                var outcome = outcomes[0];

                failReason = outcome.FailReason;

                // The authoritative single-item answer, restated from the outcome rather than left to
                // whatever the batch's aggregate happened to be. For one item the two agree by
                // construction - the aggregate IS this outcome's commit - and restating it here is what
                // keeps LastDepositCommit's contract legible at the entry point callers actually use.
                depositCommit = outcome.Commit;

                return ok;
            }
            finally
            {
                VaultDepositPhaseProfile.EndDeposit(depositScope);
            }
        }

        /// <summary>
        /// Stores N items in ONE pass, crediting the shard once per distinct (table, key) GROUP rather
        /// than once per item. SPEC-vault-batch-deposit.md sections 3 and 5 step 4.
        ///
        /// ===================== THE PARTIAL-FAILURE CONTRACT =====================
        ///
        /// UNIT OF ATOMICITY IS ONE (table, key) GROUP. Each group's delta is applied by ONE statement,
        /// which is its own implicit transaction. No BeginTransaction anywhere. A group never
        /// half-commits: all N units of a key land, or none do.
        ///
        /// ORDERING. All credits run before ANY destroy, so "ledger first, destroy second" holds across
        /// the whole sale rather than per item. A crash between the two therefore leaves up to a whole
        /// sale's dupes instead of one item's. This is the same direction the existing rule already
        /// chooses, applied to a wider window.
        ///
        /// PER-GROUP OUTCOME:
        ///
        ///   Committed - credited by the full group delta; items destroyed, one audit row each; the
        ///               per-item outcome is Committed; an operator sees one Deposit row per item.
        ///   Refused   - the ledger provably did not move; nothing destroyed, every item handed back;
        ///               the per-item outcome is None; one ERROR naming account, key, delta, item guids.
        ///   Unknown   - nobody knows; nothing destroyed, every item kept by the player; the per-item
        ///               outcome is Unknown; one STATE UNKNOWN line enumerating every item guid in the
        ///               group, plus ledger invalidation.
        ///
        /// The Unknown arm preserves the deposit/withdraw asymmetry exactly as the single-item path
        /// argues it: a visible dupe beats a silent loss, so the item is kept and nothing is destroyed.
        /// The only change is that one unknown now covers up to N items of one key, which is why the log
        /// line must ENUMERATE the guids - "+53 units of wcid 21013 is unknown" without them is not
        /// reconcilable against the biotas that survived, which is the whole point of the line.
        ///
        /// GROUPS ARE INDEPENDENT. A sale can commit group 1, refuse group 2 and leave group 3 unknown.
        /// The reported outcome is per ITEM, derived from its group.
        ///
        /// GUARANTEES AFTER A HALF-COMMITTED BATCH:
        ///
        /// 1.  Every item is in exactly one of three states: credited-and-destroyed,
        ///     kept-and-not-credited, or kept-with-an-unknown-credit. No item is destroyed without its
        ///     group's credit having committed.
        /// 1b. THE DUPE-DIRECTION MIRROR, which is the one that protects the economy: NO ITEM WHOSE
        ///     GROUP'S CREDIT COMMITTED IS EVER HANDED BACK TO THE PLAYER. Guarantee 1 is the LOSS
        ///     direction and was never the duplication risk. Both are stated because the batch changes
        ///     the shape of the disposition: with one Enqueue for the whole sale, a throw out of phase C
        ///     aborts the loop with items k..N credited, not destroyed and still detached, and the
        ///     existing per-item rule for "no recorded success" is HandBackOrphan
        ///     (PersonalVendor.cs:680) - applied unchanged to a batch, that is a hand-back of up to 512
        ///     already-credited items (ItemProfile.MaxProfilesPerTransaction = 512).
        ///     The mechanism, and it is cheap: the outcome record is initialised to "credited, do not
        ///     hand back" for EVERY item of a Committed group BEFORE phase C begins, never written as
        ///     phase C reaches each item. An abort mid-phase-C then leaves those items in the
        ///     thrown-equivalent arm and no disposition loop can hand any of them back.
        /// 2.  Every credited item has an account_vault_log row ENQUEUED before its destroy. See below
        ///     for what this does and does not guarantee.
        /// 3.  Every group whose outcome was not provably Refused or Committed produced exactly one
        ///     ERROR line naming the account, the key, the total delta and every item guid.
        /// 4.  In-memory ledger/class state after the batch comes from a fresh whole-account read, never
        ///     from arithmetic on the deltas. If that read fails the store refuses reads and mutations
        ///     until a later read succeeds. Reload failure must NOT gate the destroys, because the
        ///     credit already committed.
        ///     UNDER AN OPEN VaultDepositWindow the read is HOISTED, never skipped: the reload-pending
        ///     flags stay set for the whole window, so every reader of this store still re-reads or is
        ///     refused, and the counts they eventually see come from the window's own whole-account read.
        ///     The only thing carried across calls in the meantime is line PRESENCE - which key exists,
        ///     never how many units are on it - used by the entry-cap rule and by the DAO's "already
        ///     present" oracle. That is not arithmetic on a delta and needs no trust in one: it follows
        ///     from the database reporting the credit APPLIED, given the DAO's strictly-positive-delta
        ///     precondition and a stored count that is never negative, so an applied credit proves the row
        ///     exists with a count above zero. No count is ever synthesized, and no path that serves a
        ///     count reads any of it.
        ///
        /// WHAT GUARANTEE 2 IS NOT. WriteLog hands the row to AccountVaultAuditWriter, a background
        /// thread draining a ConcurrentQueue with a cap above which rows are DROPPED and counted. Moving
        /// the enqueue ahead of the destroy therefore moves a QUEUE PUSH, not a durable write, and a hard
        /// process kill loses the queue tail either way. The reorder trades a silent omission (credit
        /// committed, destroy threw, no row) for an affirmative overstatement (a Deposit row for an item
        /// the player still holds), which is judged the better failure: the row records the CREDIT, and
        /// the credit is what actually committed, whereas a missing credit record is not recoverable from
        /// anything, and the item's real fate stays knowable from whether the biota exists. The honest
        /// wording is that the row is enqueued once the credit is known to have committed, and the
        /// writer's own delivery guarantees are unchanged.
        ///
        /// OPERATOR RECONCILIATION. For the named account and key, sum account_vault_log Count for
        /// Deposit rows in the incident window and compare against the account_vault_stack.count /
        /// account_vault_class.count delta. Any item guid named in an UNKNOWN line that ALSO has a
        /// Deposit row is a dupe the player kept; any that does not is a credit that never happened.
        ///
        /// =======================================================================
        ///
        /// PHASES. A, per item and in order, mutating nothing in `ledger` or `classes`: the guards,
        /// IsPristine, the classify block, then the cap decision. B: one backend batch call per table.
        /// Between B and C: the initialise-before-destroy write of guarantee 1b. C, per item of every
        /// Committed group in ORIGINAL item order: enqueue the audit row, destroy, bump `version`. D: one
        /// whole-account reload per table that moved, replacing every per-item read-back.
        ///
        /// PHASE A'S PLANNING WORK IS CONTAINED PER ITEM: a throw while planning one item degrades to that
        /// item being Refused and the loop continuing, which is the property the pre-batch
        /// one-Enqueue-per-item shape had for free. DepositToVault is excluded from that containment
        /// because it is the one step of phase A that commits; the block's own remarks carry the argument.
        ///
        /// <paramref name="outcomes"/> is assigned BEFORE any work runs and is therefore readable by the
        /// caller even when this method throws - see <see cref="VaultDepositOutcome"/>, which is where
        /// guarantee 1b actually lives.
        ///
        /// Returns true only when EVERY item was accepted. A caller that deposits more than one item
        /// must read the per-item outcomes rather than this bool, which cannot say which items landed.
        /// </summary>
        internal bool TryDepositBatch(IReadOnlyList<WorldObject> items, VaultActor actor, out IReadOnlyList<VaultDepositOutcome> outcomes)
        {
            // FIRST, for the reason TryDeposit's own copy of this line gives.
            depositCommit = VaultDepositCommit.None;

            var itemCount = items?.Count ?? 0;
            var records = new VaultDepositOutcome[itemCount];

            for (var i = 0; i < itemCount; i++)
                records[i] = new VaultDepositOutcome(items[i]);

            // ASSIGNED BEFORE ANY WORK RUNS, and that is load-bearing rather than tidy. An `out`
            // parameter is a managed reference to the caller's own variable, so this write is visible to
            // the caller IMMEDIATELY - including when this method later throws, which is precisely the
            // case guarantee 1b exists for. Assigning it on the way out instead would leave the caller
            // holding nothing after a throw out of phase C, with up to 512 credited items in hand and no
            // record saying they are the vault's.
            outcomes = records;

            AssertOnMutationQueue(nameof(TryDepositBatch));

            Touch();

            if (itemCount == 0)
                return true;

            // THE TWO STORE-LEVEL GUARDS, resolved once for the whole batch rather than per item.
            // Neither depends on the item and both reach outside this store - CheckReadyLocked can start
            // a load, TryGetAccess reads account_vault_grant - so a per-item copy would put one shard
            // read on every item of a 512-item sale for an answer that cannot change inside one call:
            // every mutation for this account runs on this queue and this batch is one work item. Every
            // ITEM-level guard below stays per item, unchanged and in its original order.
            // The open coalescing window, or null. Read under the lock that guards it, in the block that was
            // already being taken, so no lock boundary moves. `tolerateDeferredReload: true` is inert
            // without one - see TryEnsureLoadedLocked(bool).
            VaultDepositWindow window;

            lock (stateLock)
            {
                if (!CheckReadyLocked(out var notReady, tolerateDeferredReload: true))
                    return RefuseWholeBatch(records, notReady);

                window = depositWindow;
            }

            // ARMED BEFORE ANYTHING CAN CLAIM, AND DISARMED HOWEVER THIS CALL LEAVES. While it is armed, a
            // re-read landing mid-call keeps this window's claims instead of dropping them, because between
            // phase A's claim and phase B's credit the line is visible to neither - see
            // VaultDepositWindow.ClaimsInFlight for why that gap is a cap BREACH rather than a double
            // charge. A `using` declaration rather than a try/finally around phases A and B: it is the same
            // guarantee on a throw, and it does not re-scope the locals phases C and D read.
            using var claimScope = ArmClaims(window);

            if (!TryGetAccess(actor, out var access, out var accessFailure))
                return RefuseWholeBatch(records, accessFailure);

            if (access == VaultAccess.None)
                return RefuseWholeBatch(records, "You do not have permission to use this vault.");

            var plans = new VaultDepositItemPlan[itemCount];

            var ledgerGroups = new Dictionary<uint, VaultLedgerDepositGroup>();
            var ledgerOrder = new List<VaultLedgerDepositGroup>();

            var classGroups = new Dictionary<string, VaultClassDepositGroup>(StringComparer.Ordinal);
            var classOrder = new List<VaultClassDepositGroup>();

            // THE BATCH-LOCAL CLAIMED SET. Phase A mutates neither `ledger` nor `classes`, so
            // AddsEntryLocked answers "yes, a new line" for the second, third and 53rd item of one new
            // wcid - each of them charging a fresh entry against the cap for a line only the first of
            // them creates. A claim is recorded per LINE: the wcid for a pristine item and the DISPLAY
            // group key for a class item, never the class key, because the class partition is strictly
            // finer than the display partition (see AddsEntryLocked's third arm) and charging per class
            // row is exactly the bug the 2026-09-25 display-key fix removed.
            //
            // AN ITEM THAT TOOK THE VAULT-CONTAINER BRANCH CLAIMS NOTHING, and that is not an oversight.
            // Its line is already in this store's own state by the time the next item is checked -
            // DepositToVault ran completely at its position and AddStoredItemToGroupingLocked placed it
            // in the grouping memo - so EntryCountLocked, which HasRoomForLocked reads, already counts
            // it. Recording a claim for it as well would count one line twice and refuse deposits that
            // fit. The claimed set exists for entries that are DEFERRED to phase B and therefore invisible
            // to the store until phase D reloads.
            //
            // CARRIED BY AN OPEN WINDOW, because its lifetime is exactly "lines this store has created that
            // its own re-read has not yet made visible", and a window is precisely the period during which
            // that re-read is deferred. Without the carry, item 2 of a 50-item delivery of one new wcid
            // would be charged a fresh entry for the line item 1 created, and so on for all 50. Nothing but
            // PRESENCE is carried - see VaultEntryClaims.
            //
            // ONE SET PER TABLE, because the two tables are re-read INDEPENDENTLY and a set outlives its
            // usefulness the instant its own table reloads - at which point its entries are counted a second
            // time on top of EntryCountLocked(). The reload routines drop each table's claims with that
            // table's presence, in the same statement, which is only expressible with the two kept apart.
            // That drop is held back while THIS call's credits are in flight - see ArmClaims - because a
            // claim dropped before its row exists is a line nothing counts at all.
            var ledgerClaims = window != null ? window.LedgerClaims : new VaultEntryClaims();
            var classClaims = window != null ? window.ClassClaims : new VaultEntryClaims();

            // ================================ PHASE A ================================

            for (var i = 0; i < itemCount; i++)
            {
                var item = items[i];
                var record = records[i];

                // ================== PHASE A'S PER-ITEM CONTAINMENT ==================
                //
                // ONE ITEM'S PLANNING FAILURE MUST NOT ABANDON THE REST OF THE SALE, and this catch
                // RESTORES a property the batching removed rather than adding a new one. Before the batch
                // every item had its own Enqueue and its own TryDeposit call, so a throw while planning
                // item 5 of 10 left items 6..10 untouched, undestroyed and provably still the caller's.
                // One Enqueue for the whole sale erases that boundary: without this catch the same throw
                // unwinds out of TryDepositBatch with items 6..10 never examined - detached, not
                // destroyed, in no vault container, and carrying an outcome record nobody may act on.
                // That is a state outside all three guarantee 1 enumerates.
                //
                // WHAT IT COVERS IS EXACTLY WHAT CANNOT COMMIT, and the scope is the whole argument.
                // Everything inside this block READS the item and writes nothing outside batch-local plan
                // state: the item guards, IsPristine, TryDescribeClass, the cap decision (lock-held reads
                // of `ledger`, `classes` and the grouping memo), ClassRoundTripHolds - which builds and
                // destroys its own scratch probe and never touches this item - and the construction of
                // the deferred groups. No shard statement runs, no container is mutated and this item's
                // biota is not touched. So a throw here PROVES no credit was attempted for it, which is
                // precisely what Refused/None means, and the item goes home rather than being withheld.
                //
                // DepositToVault sits deliberately OUTSIDE, below the catch. It is the one step of phase A
                // that commits, and a throw out of it must keep unwinding the whole call - see its own
                // remarks. Keeping it out is what lets this catch write None honestly; swallowing it would
                // both widen None to a case that can be half-committed and silently convert TryDeposit's
                // throw contract into a plain false, which the market's delivery path reads.
                //
                // Every guard arm above CONTINUES out of this block, so a record that reaches the catch is
                // normally still at its initialised None; the catch writes None again with the generic
                // message. The one overwrite is a throw from the cap scope's own finally after a cap
                // refusal, which replaces the full-vault message with the generic one - same disposition,
                // less specific text.
                try
                {
                    if (item == null)
                    {
                        record.Refuse("There is nothing to store.");
                        continue;
                    }

                    if (item.UseBackpackSlot)
                    {
                        // UseBackpackSlot is WeenieType == Container OR RequiresPackSlot
                        // (WorldObject_Properties.cs:2070, :1813), and the two are refused for different
                        // reasons, so they must not share a message. A Focusing Stone occupies a pack slot
                        // without being a pack, and telling its owner to "store what is inside it" is advice
                        // they cannot act on.
                        //
                        // Vault containers pin ContainerCapacity to 0, so TryAddToInventory would refuse a
                        // pack anyway. Refusing here instead gives the player the real reason rather than the
                        // generic "unavailable", and it is where the rule is decided rather than where it
                        // happens to be enforced.
                        record.Refuse(item.WeenieType == WeenieType.Container
                            ? "A pack cannot be stored in your vault. Store what is inside it instead."
                            : "That takes up a pack slot of its own and cannot be stored in your vault.");

                        continue;
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
                        // now. The clock restarts. Vault containers are also never ticked, so a stored one
                        // does not expire while it sits there either.
                        //
                        // Refused here rather than "fixed" in VaultCollapse: diffing on CreationTimestamp
                        // would make every ordinary stack unique to its own creation moment and stop the
                        // ledger firing at all (DESIGN section 8 is explicit that collapse is a diff, not a
                        // blacklist, and that exemption is what makes it work). The refusal is a rule about
                        // what may be STORED, which is this method's business, and it is mirrored verbatim in
                        // PersonalVendor.CanAcceptCore so the pre-flight and the store never disagree.
                        record.Refuse("A timed item cannot be stored in your vault.");
                        continue;
                    }

                    if (item.ItemType == ItemType.PromissoryNote)
                    {
                        // A trade note never becomes vault content: PersonalVendor.DepositItems diverts it to
                        // the player's banked pyreals at face value (Player.BankTradeNote) before it is ever
                        // offered to a store, so the vendor path does not reach this line at all. It is here
                        // as defence in depth for any OTHER caller, present or future.
                        //
                        // Why a note must not be stored: withdrawal is free on the server, but the client's
                        // vendor panel gates every row on the player being able to afford that row's Value in
                        // coin, and the client is never patched. A stored Trade Note (250,000) is therefore
                        // unrecoverable by anyone not already carrying 250,000 pyreals - the item is in the
                        // vault and the player cannot take it out. Banking it is exact (item.Value is the
                        // whole stack's value) and leaves the same purchasing power withdrawable at will.
                        //
                        // THIS IS THE ONE STORE REFUSAL DELIBERATELY NOT MIRRORED IN
                        // PersonalVendor.CanAcceptCore. Every other refusal here is mirrored there so an item
                        // is never detached from the player for a deposit the store will reject. A note must
                        // do the opposite: it has to PASS the pre-flight to reach the sell list, because
                        // being banked is the outcome the player wants. A copy of this guard in CanAcceptCore
                        // would refuse the note at the panel and the divert would become dead code.
                        record.Refuse("Trade notes are banked, not stored.");
                        continue;
                    }

                    if (item.ContainerId != null || item.WielderId != null)
                    {
                        log.Error($"[VAULT] account {AccountId}: refused a deposit of 0x{item.Guid.Full:X8} because it is still parented (container {item.ContainerId}, wielder {item.WielderId}). The caller must detach it first.");
                        record.Refuse(UnavailableMessage);
                        continue;
                    }

                    // OUTSIDE every lock, deliberately. IsPristine takes the item's own BiotaDatabaseLock read
                    // lock, and BiotaDatabaseLock is a default-policy ReaderWriterLockSlim (NoRecursion), so a
                    // caller already holding that item's lock would make the acquisition throw, have it
                    // swallowed by IsPristine's own catch, and get false back on every single call - a silent,
                    // total failure of the ledger that presents as "nothing ever collapses".
                    //
                    // It runs BEFORE the cap check because the cap check needs its answer. It is still outside
                    // every lock in this class and the store still never takes a BiotaDatabaseLock.
                    var pristineScope = VaultDepositPhaseProfile.Begin();

                    var pristine = world.IsPristine(item);

                    VaultDepositPhaseProfile.End(VaultDepositPhase.Pristine, pristineScope);

                    // ONE call to the class predicate per ITEM, on the same rule and for the same reason as
                    // IsPristine above: TryDescribeClass takes the item's own BiotaDatabaseLock read lock and
                    // this store must never hold stateLock across that. It is skipped entirely for a pristine
                    // item, which is going to the stack ledger and can never be a class.
                    //
                    // ITS ANSWER IS CARRIED FORWARD, NEVER RECOMPUTED AND NEVER CACHED ACROSS A GAP.
                    // Recomputing it below the cap check would double the most expensive thing on this path;
                    // caching it from a caller's earlier pre-flight would be the TOCTOU HasObviousHeadroom's
                    // remarks describe, where a stale true collapses an item that has since changed and
                    // destroys it.
                    VaultItemClassOverrides classOverrides = null;
                    string classKey = null;
                    string classDisplayKey = null;

                    var classifyScope = VaultDepositPhaseProfile.Begin();

                    if (!pristine && ClassStorageEnabled && world.TryDescribeClass(item, out var described))
                    {
                        classOverrides = described;
                        classKey = VaultItemClass.ClassKey(item.WeenieClassId, described, VaultItemClass.ValueExcluded);

                        // The DISPLAY key the cap rule needs, off the payload that predicate call already
                        // produced, so it costs no second TryDescribeClass.
                        classDisplayKey = VaultCollapse.ClassDisplayGroupKey(item.WeenieClassId, described);
                    }

                    VaultDepositPhaseProfile.End(VaultDepositPhase.Classify, classifyScope);

                    // The cap counts ENTRIES, not units (DESIGN 7.3), so it may only refuse a deposit that
                    // actually ADDS one. A pristine item topping up a ledger row the account already holds
                    // creates no row and no entry, and refusing it is the cap doing the opposite of what 7.3
                    // says it is for: an account at 500 of 500 with 9,000 healing kits on one row was being
                    // told its vault was full for the 9,001st, which is exactly the hoarding case collapse
                    // exists to make free. The rule itself is AddsEntryLocked, shared with WouldAddEntry so
                    // the two cannot drift apart again.
                    //
                    // VaultDepositPhase.Cap, INCLUDING the stateLock acquisition - see the phase's own remarks
                    // for why waiting on a contended store is charged to the deposit rather than hidden.
                    string claimKey = null;
                    var addsEntry = false;
                    var refusedByCap = false;

                    // WHICH TABLE'S CLAIM SET THIS ITEM BELONGS TO, decided by the one fact that also decides
                    // its branch: a pristine item collapses to account_vault_stack, anything else that claims
                    // at all collapses to account_vault_class. Chosen ONCE, here, and used for both the
                    // lookup below and the ClaimEntry call further down, so the set a key is looked up in
                    // can never be a different set from the one it was recorded in. Never re-derived by
                    // parsing the claim key's "w:"/"d:" prefix - the prefix exists to keep the two key
                    // spaces from colliding, not to carry routing.
                    //
                    // A vault-container item reaches neither ClaimEntry call, so its set is never used; see
                    // the claimed set's remarks above for why it claims nothing.
                    var itemClaims = pristine ? ledgerClaims : classClaims;

                    var capScope = VaultDepositPhaseProfile.Begin();

                    try
                    {
                        lock (stateLock)
                        {
                            addsEntry = AddsEntryLocked(pristine, item, classKey, classDisplayKey);

                            if (addsEntry)
                            {
                                claimKey = pristine
                                    ? $"w:{item.WeenieClassId}"
                                    : classKey != null && classDisplayKey != null ? $"d:{classDisplayKey}" : null;

                                // An earlier item of THIS batch already claimed this line, so this one lands
                                // on a row that will exist by the time the credit runs and costs no entry.
                                if (claimKey != null && itemClaims.Keys.Contains(claimKey))
                                    addsEntry = false;
                            }

                            // BOTH tables' claims, because the cap bounds PANEL LINES across the whole
                            // account and does not care which table a line is drawn from. Each set is
                            // dropped when its own table is re-read, so once a batch's credits have
                            // resolved this sum counts only lines EntryCountLocked() cannot see yet. While
                            // a batch is mid-flight the drop is held back (ClaimsInFlight), so the sum can
                            // count one of this delivery's own lines twice - deliberately, because the
                            // alternative is counting it zero times and admitting a line past the cap.
                            var claimedEntries = ledgerClaims.Entries + classClaims.Entries;

                            if (addsEntry && !HasRoomForLocked(claimedEntries + 1))
                            {
                                // The claims are added to the reported figure for the same reason they are
                                // added to the test above: they are lines this account already holds, whose
                                // rows exist in the database and are simply not in the in-memory count yet.
                                // Reporting EntryCountLocked() alone tells a refused player they hold fewer
                                // entries than their cap while refusing them for being at it, which reads as
                                // a bug in the cap rather than as a full vault.
                                var held = EntryCountLocked() + claimedEntries;
                                var cap = EffectiveEntryCap;

                                record.Refuse($"{FullMessagePrefix} {held} of {cap} entries. Withdraw something before storing more.");
                                refusedByCap = true;
                            }
                        }
                    }
                    finally
                    {
                        VaultDepositPhaseProfile.End(VaultDepositPhase.Cap, capScope);
                    }

                    if (refusedByCap)
                        continue;

                    if (pristine)
                    {
                        var units = item.StackSize ?? 1;

                        if (units < 1)
                            units = 1;

                        plans[i] = new VaultDepositItemPlan
                        {
                            Item = item,
                            Wcid = item.WeenieClassId,
                            Name = item.Name,
                            Units = units,
                        };

                        // Built from a DICTIONARY, so a repeated key is impossible by construction rather than
                        // caught by an assert - the DAO's distinct-key precondition is a correctness
                        // requirement (MySQL's CASE takes the first matching WHEN), not hygiene.
                        if (!ledgerGroups.TryGetValue(item.WeenieClassId, out var ledgerGroup))
                        {
                            ledgerGroup = new VaultLedgerDepositGroup { Wcid = item.WeenieClassId };

                            ledgerGroups.Add(item.WeenieClassId, ledgerGroup);
                            ledgerOrder.Add(ledgerGroup);
                        }

                        // THE INDEX IS ADDED BEFORE THE DELTA, so this pair is effectively atomic under the
                        // per-item catch above. List.Add is the only throwable half (an allocation
                        // failure); running it first means a throw leaves the group with neither this
                        // item's index nor its units. The other order would credit units for an item the
                        // group never lists, which phase C would then never destroy - an over-credit, the
                        // one direction that mints.
                        ledgerGroup.ItemIndexes.Add(i);
                        ledgerGroup.Units += units;

                        // itemClaims is ledgerClaims here by construction (this arm is the pristine one),
                        // and it is passed rather than named directly so the set a key is recorded in is
                        // provably the set the lookup above read.
                        ClaimEntry(addsEntry, claimKey, itemClaims);

                        continue;
                    }

                    if (classKey != null)
                    {
                        // THE ROUND-TRIP SELF-CHECK RUNS BEFORE ANYTHING IS CREDITED OR DESTROYED, and it is
                        // the guarantee that makes destroying the biota defensible at all. TryDescribeClass
                        // proves the item differs from its template only on the whitelist; this proves the
                        // other half, that replaying the payload onto a fresh template really does produce an
                        // item of the SAME class. If it does not, the item keeps its biota and goes to a vault
                        // container instead. Falling back costs one entry and nothing else; guessing costs the
                        // player their item.
                        var roundTripScope = VaultDepositPhaseProfile.Begin();

                        var roundTripHolds = ClassRoundTripHolds(item.WeenieClassId, classOverrides, classKey);

                        VaultDepositPhaseProfile.End(VaultDepositPhase.RoundTrip, roundTripScope);

                        if (roundTripHolds)
                        {
                            // The amount the pool gains. Read off the item BEFORE anything is destroyed, and
                            // clamped at zero because total_Value carries a non-negative guard in the
                            // database. Zero is legitimate and is why the batch DAO's VALUE delta rule is
                            // non-negative while its COUNT delta rule is strictly positive.
                            var value = item.Value ?? 0;

                            if (value < 0)
                                value = 0;

                            plans[i] = new VaultDepositItemPlan
                            {
                                Item = item,
                                Wcid = item.WeenieClassId,
                                Name = item.Name,
                                Units = 1,
                                Value = value,
                                ClassKey = classKey,
                            };

                            if (!classGroups.TryGetValue(classKey, out var classGroup))
                            {
                                classGroup = new VaultClassDepositGroup
                                {
                                    ClassKey = classKey,
                                    Wcid = item.WeenieClassId,
                                    Overrides = classOverrides,
                                    Seed = new AccountVaultClassSeed
                                    {
                                        Wcid = item.WeenieClassId,
                                        ValueBandPct = VaultItemClass.ValueExcluded,
                                        CanonicalForm = VaultItemClass.CanonicalForm(item.WeenieClassId, classOverrides, VaultItemClass.ValueExcluded),
                                    },
                                };

                                classGroups.Add(classKey, classGroup);
                                classOrder.Add(classGroup);
                            }

                            // Index first, deltas second, for the reason the ledger group's own remark
                            // gives: it makes the pair atomic under the per-item catch.
                            classGroup.ItemIndexes.Add(i);
                            classGroup.CountDelta++;
                            classGroup.ValueDelta += value;

                            // itemClaims is classClaims here by construction (this arm is reached only when
                            // the item is not pristine). This is also the one call that can carry a NULL
                            // claim key - a class line whose display key could not be derived - and it must
                            // still charge its entry, against this table, because the line is real either
                            // way; it simply cannot be shared with a later item.
                            ClaimEntry(addsEntry, claimKey, itemClaims);

                            continue;
                        }

                        log.Warn($"[VAULT] account {AccountId}: 0x{item.Guid.Full:X8} ({item.Name}, wcid {item.WeenieClassId}) passed the class predicate but does NOT rebuild to its own class key, so it is being stored as an ordinary biota instead. Nothing was destroyed.");

                        // Deliberately NO ClaimEntry here, even though the cap was evaluated on the class
                        // assumption: this item is about to take the vault branch, and that branch's line is
                        // counted by EntryCountLocked itself the moment it lands. See the claimed set's own
                        // remarks.
                    }

                }
                catch (Exception ex)
                {
                    log.Error($"[VAULT] account {AccountId}: PLANNING 0x{item?.Guid.Full ?? 0:X8} ({item?.Name}) for a batched deposit THREW. Only this item fails - the rest of the sale continues - and it is REFUSED rather than left unknown, because everything inside this block reads the item and writes nothing but batch-local plan state, so nothing can have been credited for it. {ex}");

                    record.Refuse(UnavailableMessage);

                    continue;
                }

                // The vault-container branch, executed IMMEDIATELY and COMPLETELY at this item's position,
                // because it mutates a container and cannot be deferred to phase B. It records its own
                // commit into depositCommit, which the aggregate below preserves.
                //
                // DELIBERATELY OUTSIDE THE CONTAINMENT ABOVE, and that placement is the containment's whole
                // safety argument - see its remarks. A throw from here still unwinds the entire call, which
                // is what the single-item contract requires: VaultMarketItemStore.TryGiveToBuyer catches a
                // throw out of TryDeposit and reads LastDepositCommit to decide whose the item is, and
                // MarketPartialDeliveryTests pins that a save failure in this tail stops the group's
                // delivery rather than being swallowed into a plain false.
                if (DepositToVault(item, actor, out var vaultFailure))
                    record.MarkStored();
                else
                    record.Refuse(vaultFailure);
            }

            // ================================ PHASE B ================================
            //
            // One backend call per TABLE. Each group inside it is independent, and the per-key answer is
            // the real one - the bool the DAO returns only says "every key applied".

            var ledgerMoved = false;
            var classMoved = false;

            // Keys this call PROVED present, collected here and merged into the window in phase D under the
            // lock that block already takes. Only an outcome the database reported applied qualifies: every
            // delta is strictly positive and no stored count is negative, so an applied credit means the row
            // exists with a count above zero, which is exactly what the DAO's oracle means by "present".
            List<uint> provedLedgerKeys = null;
            List<string> provedClassKeys = null;

            if (ledgerOrder.Count > 0)
            {
                var deltas = new List<(uint Wcid, long Delta)>(ledgerOrder.Count);

                foreach (var group in ledgerOrder)
                    deltas.Add((group.Wcid, group.Units));

                HashSet<uint> knownPresent;

                lock (stateLock)
                {
                    knownPresent = new HashSet<uint>();

                    foreach (var row in ledger)
                    {
                        if (row.Value > 0)
                            knownPresent.Add(row.Key);
                    }

                    // Plus the rows an open window has already proved present but whose deferred re-read has
                    // not yet put back into `ledger`. Omitting them would not be a correctness bug - the DAO
                    // would INSERT..ON DUPLICATE KEY UPDATE them and land the same delta - but every such
                    // call burns an AUTO_INCREMENT id for a row that already exists, and account_vault_stack.id
                    // is int unsigned, where exhaustion is a permanent outage.
                    if (window != null)
                    {
                        foreach (var wcid in window.LedgerPresent)
                            knownPresent.Add(wcid);
                    }
                }

                // IN-FLIGHT, written BEFORE the statement runs, and for exactly the reason the single-item
                // path set depositCommit = Unknown before its own adjust: a THROW out of this call leaves
                // nobody knowing whether the credit landed, and every one of these items must then be KEPT
                // rather than read as refused and handed back.
                MarkLedgerGroupsUnknown(ledgerOrder, records);

                if (depositCommit == VaultDepositCommit.None)
                    depositCommit = VaultDepositCommit.Unknown;

                IReadOnlyDictionary<uint, AccountVaultStackAdjustResult> perKey = null;

                var upsertScope = VaultDepositPhaseProfile.Begin();

                try
                {
                    backend.TryAdjustAccountVaultStackBatch(AccountId, deltas, knownPresent, out perKey);
                }
                finally
                {
                    // EndForGroups, not End: ONE multi-row statement stands in for what used to be one
                    // statement per group, so vd_upsert_n is advanced by the GROUP count while the region's
                    // elapsed time is charged exactly once. The ms total - and therefore the "phases plus
                    // remainder equals vd_ms" invariant - is untouched; only the call count changes, so
                    // vd_upsert_n / vd_n reads as the batch's compression ratio straight off the line.
                    VaultDepositPhaseProfile.EndForGroups(VaultDepositPhase.Upsert, upsertScope, ledgerOrder.Count);
                }

                foreach (var group in ledgerOrder)
                {
                    // A key the DAO answered for is authoritative; a key it did not answer for is Failed,
                    // never Applied. The contract says every supplied key is populated, so this arm should
                    // be unreachable - it is here because the alternative to a missing answer being Failed
                    // is a missing answer destroying an item.
                    var outcome = perKey != null && perKey.TryGetValue(group.Wcid, out var found)
                        ? found
                        : AccountVaultStackAdjustResult.Failed;

                    if (outcome != AccountVaultStackAdjustResult.Refused)
                        ledgerMoved = true;

                    if (window != null && (outcome == AccountVaultStackAdjustResult.Applied || outcome == AccountVaultStackAdjustResult.AppliedCountUnknown))
                        (provedLedgerKeys ?? (provedLedgerKeys = new List<uint>())).Add(group.Wcid);

                    ResolveLedgerGroup(group, outcome, records, plans);
                }

                depositCommit = AggregateCommit(records);
            }

            if (classOrder.Count > 0)
            {
                var groups = new List<(string ClassKey, long CountDelta, long ValueDelta, AccountVaultClassSeed Seed)>(classOrder.Count);

                foreach (var group in classOrder)
                    groups.Add((group.ClassKey, group.CountDelta, group.ValueDelta, group.Seed));

                HashSet<string> knownPresent;

                lock (stateLock)
                {
                    knownPresent = new HashSet<string>(classes.Keys, StringComparer.Ordinal);

                    // The stack table's own oracle carries the same addition, for the same reason.
                    if (window != null)
                    {
                        foreach (var classKey in window.ClassPresent)
                            knownPresent.Add(classKey);
                    }
                }

                MarkClassGroupsUnknown(classOrder, records);

                if (depositCommit == VaultDepositCommit.None)
                    depositCommit = VaultDepositCommit.Unknown;

                IReadOnlyDictionary<string, AccountVaultStackAdjustResult> perKey = null;

                var upsertScope = VaultDepositPhaseProfile.Begin();

                try
                {
                    backend.TryAdjustAccountVaultClassBatch(AccountId, groups, knownPresent, out perKey);
                }
                finally
                {
                    // EndForGroups, for the reason the stack table's own call site gives. The two tables
                    // charge the SAME phase, so a mixed sale's vd_upsert_n is the total group count across
                    // both - which is what makes the ratio against vd_n mean one thing rather than two.
                    VaultDepositPhaseProfile.EndForGroups(VaultDepositPhase.Upsert, upsertScope, classOrder.Count);
                }

                foreach (var group in classOrder)
                {
                    var outcome = perKey != null && perKey.TryGetValue(group.ClassKey, out var found)
                        ? found
                        : AccountVaultStackAdjustResult.Failed;

                    if (outcome != AccountVaultStackAdjustResult.Refused)
                        classMoved = true;

                    if (window != null && (outcome == AccountVaultStackAdjustResult.Applied || outcome == AccountVaultStackAdjustResult.AppliedCountUnknown))
                        (provedClassKeys ?? (provedClassKeys = new List<string>())).Add(group.ClassKey);

                    ResolveClassGroup(group, outcome, records, plans);
                }
            }

            // The aggregate, which for ONE item is exactly what the single-item path wrote into
            // depositCommit at each of its own three arms: None for a provable refusal, Unknown for an
            // outcome nobody could establish, Committed once the credit landed or the biota reached a
            // vault container. For a batch it is the most-committed state any item reached, which is the
            // only reading of a single field that cannot tell a caller to hand back something credited.
            depositCommit = AggregateCommit(records);

            // PHASE B HAS RESOLVED, so every claim this call recorded now describes a row the database has
            // either created or refused, and a re-read from here on is free to drop them: it can see the
            // created ones itself, and a refused group's claim describes a line that does not exist and
            // should not be charged. Disarmed HERE rather than left to the scope's Dispose so the window
            // spends the shortest possible time in the over-counting mode. The Dispose still runs and is a
            // no-op; it exists for the paths that never reach this line.
            claimScope?.Disarm();

            // ================================ PHASE C ================================
            //
            // Per item of every Committed group, in ORIGINAL item order. Nothing here decides anything:
            // the outcome records were written in phase B and this loop only reads them, which is what
            // makes guarantee 1b hold under a throw from any line in it.

            try
            {
                for (var i = 0; i < itemCount; i++)
                {
                    var plan = plans[i];

                    // plan == null covers every item that never reached a deferred group: a guard refused
                    // it, the cap refused it, or it took the vault-container branch and is already stored.
                    if (plan == null || records[i].Commit != VaultDepositCommit.Committed)
                        continue;

                    // AUDIT FIRST, DESTROY SECOND - the reverse of the single-item path, and deliberate.
                    // A destroy that throws used to lose the audit row for a credit that HAD committed,
                    // which is the one artifact an operator needs. See "WHAT GUARANTEE 2 IS NOT" above for
                    // what this reorder does and does not buy.
                    var auditScope = VaultDepositPhaseProfile.Begin();

                    if (plan.ClassKey == null)
                        WriteLog(AccountVaultAction.Deposit, actor, plan.Wcid, null, plan.Name, plan.Units);
                    else
                        WriteLog(AccountVaultAction.Deposit, actor, plan.Wcid, null, plan.Name, plan.Units, plan.Value, plan.ClassKey);

                    VaultDepositPhaseProfile.End(VaultDepositPhase.Audit, auditScope);

                    var destroyScope = VaultDepositPhaseProfile.Begin();

                    world.DestroyItem(plan.Item);

                    VaultDepositPhaseProfile.End(VaultDepositPhase.Destroy, destroyScope);

                    Interlocked.Increment(ref version);
                }
            }
            finally
            {
                // IN A FINALLY, so a throw out of phase C cannot leave this store serving a count it knows
                // is stale. The flags are what TryEnsureLoadedLocked reads: with one set and the reload
                // never run, the store refuses reads and mutations until a later read succeeds, which is
                // guarantee 4's failure mode rather than a new one. On the ordinary path phase D clears
                // them on the very next statement.
                lock (stateLock)
                {
                    if (ledgerMoved)
                        ledgerReloadPending = true;

                    if (classMoved)
                        classReloadPending = true;
                }
            }

            // ================================ PHASE D ================================
            //
            // ONE whole-account read per table that moved, replacing every per-item read-back. Guarantee
            // 4: the in-memory state after a batch comes from a fresh read and never from arithmetic on
            // the deltas.

            // VaultDepositPhase.Reload, INCLUDING the stateLock acquisition, for the reason
            // VaultDepositPhase.Cap gives: waiting on a contended store is part of what the deposit cost and
            // is charged to it rather than hidden. Charged once per call that REACHED phase D, however many
            // tables reloaded and however many items the batch carried - the phase's own remarks pin that
            // reading. Straight-line between Begin and End, so a try/finally would buy nothing: a throw out
            // of the reload unwinds past this charge and past TryDeposit's own scope, whose finally is what
            // guarantees the instrument is disarmed either way.
            var reloadScope = VaultDepositPhaseProfile.Begin();

            lock (stateLock)
            {
                // AN OPEN WINDOW HOISTS THE RE-READ TO ITS CLOSE, and hoists nothing else. The pending flags
                // are already set (phase C's finally), and they STAY set, so any other reader of this store
                // still re-reads or is refused and can never be served a count known to be stale. What moves
                // is only WHERE the one read happens: once for the window rather than once per call. See
                // VaultDepositWindow.
                //
                // GUARANTEE 4 IS UNTOUCHED. The in-memory counts still come from a fresh whole-account read
                // and never from arithmetic on the deltas; that read is simply the window's. What is carried
                // forward in the meantime is line PRESENCE and nothing else - which key exists, never how
                // many units are on it - and presence is not arithmetic on a delta: it follows from the
                // database reporting the credit applied, given the DAO's own strictly-positive-delta
                // precondition and the non-negative stored count.
                if (window != null && ReferenceEquals(depositWindow, window))
                {
                    if (provedLedgerKeys != null)
                    {
                        foreach (var wcid in provedLedgerKeys)
                            window.LedgerPresent.Add(wcid);
                    }

                    if (provedClassKeys != null)
                    {
                        foreach (var classKey in provedClassKeys)
                            window.ClassPresent.Add(classKey);
                    }

                    if (ledgerMoved)
                        window.DeferredLedgerReload = true;

                    if (classMoved)
                        window.DeferredClassReload = true;
                }
                else
                {
                    if (ledgerMoved && !TryReloadLedgerLocked())
                        log.Error($"[VAULT] account {AccountId}: could not re-read the stack ledger after a batched deposit of {ledgerOrder.Count} key(s). The store will refuse reads and mutations until a later read succeeds, rather than serve a count it knows is stale. THE DEPOSIT STANDS - the credit committed and the items are gone - and this failure must never be read as an undo.");

                    if (classMoved && !TryReloadClassesLocked())
                        log.Error($"[VAULT] account {AccountId}: could not re-read the item-class ledger after a batched deposit of {classOrder.Count} key(s). The store will refuse reads and mutations until a later read succeeds, rather than serve a count it knows is stale. THE DEPOSIT STANDS - the credit committed and the items are gone - and this failure must never be read as an undo.");
                }
            }

            VaultDepositPhaseProfile.End(VaultDepositPhase.Reload, reloadScope);

            foreach (var record in records)
            {
                if (!record.Deposited)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Records a batch-local claim for the LINE this item creates, so a later item of the same line
        /// is not charged a second entry for it. Called only after the item's route is known to be
        /// DEFERRED to phase B - see the claimed set's remarks in <see cref="TryDepositBatch"/>.
        ///
        /// <paramref name="claims"/> is the set for the TABLE this item routes to - the batch's own, or an
        /// open window's when one is carrying them across several batches. The caller picks it from the
        /// same `pristine` flag that picks the branch, rather than this method re-deriving it from the
        /// claim key's prefix, so a key can never be recorded in one set and looked up in another. Either
        /// way it is touched only from the mutation queue, under the drain lock, so it needs no lock of its
        /// own.
        /// </summary>
        private static void ClaimEntry(bool addsEntry, string claimKey, VaultEntryClaims claims)
        {
            if (!addsEntry)
                return;

            claims.Entries++;

            // A null claim key is a line this batch cannot recognise again (a class item whose display key
            // could not be derived). It still consumes an entry; it simply cannot be shared, which is the
            // conservative direction and matches AddsEntryLocked's own answer for that case.
            if (claimKey != null)
                claims.Keys.Add(claimKey);
        }

        /// <summary>
        /// Marks <paramref name="window"/>'s cap claims as IN FLIGHT and hands back the scope that clears
        /// the mark. Null in, null out - a deposit with no open window carries its claims in locals that
        /// die with the call, so no reload can reach them and there is nothing to protect.
        ///
        /// Both halves run under <see cref="stateLock"/>, which is the lock the reload routines that read
        /// the flag already hold. That is what makes the two orderings safe rather than merely likely: a
        /// reload that observed the flag CLEAR has finished its own claim clear before this method can
        /// return, so it cannot be clearing a set while phase A adds to it, and a reload that arrives after
        /// this returns observes it SET and leaves the claims alone. It also closes the plain data race the
        /// unconditional clear had with phase A's lock-free ClaimEntry.
        /// </summary>
        private VaultClaimScope ArmClaims(VaultDepositWindow window)
        {
            if (window == null)
                return null;

            lock (stateLock)
                window.ClaimsInFlight = true;

            return new VaultClaimScope(() =>
            {
                lock (stateLock)
                    window.ClaimsInFlight = false;
            });
        }

        /// <summary>Refuses every item of a batch with one reason, for a STORE-level guard that fails the whole call.</summary>
        private static bool RefuseWholeBatch(VaultDepositOutcome[] records, string failReason)
        {
            foreach (var record in records)
                record.Refuse(failReason);

            return false;
        }

        /// <summary>
        /// The most-committed state any item of this batch reached. None &lt; Unknown &lt; Committed, so
        /// this can never report a batch less committed than it is - which is the only direction that
        /// matters, because a caller reads it to decide whether it may hand something back.
        /// </summary>
        private static VaultDepositCommit AggregateCommit(VaultDepositOutcome[] records)
        {
            var aggregate = VaultDepositCommit.None;

            foreach (var record in records)
            {
                if (record.Commit > aggregate)
                    aggregate = record.Commit;
            }

            return aggregate;
        }

        private static void MarkLedgerGroupsUnknown(List<VaultLedgerDepositGroup> groups, VaultDepositOutcome[] records)
        {
            foreach (var group in groups)
            {
                foreach (var index in group.ItemIndexes)
                    records[index].MarkUnknown(UnavailableMessage);
            }
        }

        private static void MarkClassGroupsUnknown(List<VaultClassDepositGroup> groups, VaultDepositOutcome[] records)
        {
            foreach (var group in groups)
            {
                foreach (var index in group.ItemIndexes)
                    records[index].MarkUnknown(UnavailableMessage);
            }
        }

        /// <summary>Every item guid in one group, for the ERROR line guarantee 3 requires.</summary>
        private static string DescribeGroupItems(List<int> itemIndexes, VaultDepositItemPlan[] plans)
        {
            return string.Join(", ", itemIndexes.Select(index => $"0x{plans[index].Item.Guid.Full:X8}"));
        }

        /// <summary>
        /// Maps one ledger group's database outcome onto every item that fed it.
        ///
        /// THE APPLIED ARM IS THE INITIALISE-BEFORE-PHASE-C WRITE of guarantee 1b: it runs here, for the
        /// whole group at once, and NOT as phase C reaches each item. See <see cref="VaultDepositOutcome"/>.
        /// </summary>
        private void ResolveLedgerGroup(VaultLedgerDepositGroup group, AccountVaultStackAdjustResult outcome,
                                        VaultDepositOutcome[] records, VaultDepositItemPlan[] plans)
        {
            if (outcome == AccountVaultStackAdjustResult.Refused)
            {
                log.Error($"[VAULT] account {AccountId}: the ledger REFUSED a deposit of {group.Units} unit(s) of wcid {group.Wcid} across {group.ItemIndexes.Count} item(s). The ledger provably did not move, nothing was destroyed and the player keeps every one of these items: {DescribeGroupItems(group.ItemIndexes, plans)}.");

                foreach (var index in group.ItemIndexes)
                    records[index].Refuse(UnavailableMessage);

                return;
            }

            if (outcome == AccountVaultStackAdjustResult.Failed)
            {
                log.Error($"[VAULT] LEDGER STATE UNKNOWN: account {AccountId}, wcid {group.Wcid}, delta +{group.Units} across {group.ItemIndexes.Count} item(s). A deposit credit neither committed nor provably failed. NOTHING was destroyed and the player keeps every one of these items: {DescribeGroupItems(group.ItemIndexes, plans)}. If the credit DID land, this account now holds those units twice. To reconcile: sum account_vault_log Count over Deposit rows for this account and wcid in the incident window and compare against the account_vault_stack.count delta - any guid named here that ALSO has a Deposit row is a dupe the player kept, and any that does not is a credit that never happened.");

                foreach (var index in group.ItemIndexes)
                    records[index].MarkUnknown(UnavailableMessage);

                return;
            }

            // Applied, or applied with a count nobody could read back: either way the units ARE in the
            // ledger, and phase D's whole-account reload is what supplies the count.
            foreach (var index in group.ItemIndexes)
                records[index].MarkCredited();
        }

        /// <summary>
        /// <see cref="ResolveLedgerGroup"/>'s counterpart for account_vault_class, identical on every
        /// point that matters: the same three-outcome mapping, the same guid-enumerating ERROR line, and
        /// the same initialise-before-phase-C write on the committed arm.
        /// </summary>
        private void ResolveClassGroup(VaultClassDepositGroup group, AccountVaultStackAdjustResult outcome,
                                       VaultDepositOutcome[] records, VaultDepositItemPlan[] plans)
        {
            if (outcome == AccountVaultStackAdjustResult.Refused)
            {
                log.Error($"[VAULT] account {AccountId}: the class ledger REFUSED a deposit of {group.CountDelta} item(s) / +{group.ValueDelta} value into class {group.ClassKey} (wcid {group.Wcid}). The row provably did not move, nothing was destroyed and the player keeps every one of these items: {DescribeGroupItems(group.ItemIndexes, plans)}.");

                foreach (var index in group.ItemIndexes)
                    records[index].Refuse(UnavailableMessage);

                return;
            }

            if (outcome == AccountVaultStackAdjustResult.Failed)
            {
                log.Error($"[VAULT] CLASS STATE UNKNOWN: account {AccountId}, class {group.ClassKey}, wcid {group.Wcid}, delta +{group.CountDelta} item(s) / +{group.ValueDelta} value. A deposit credit neither committed nor provably failed. NOTHING was destroyed and the player keeps every one of these items: {DescribeGroupItems(group.ItemIndexes, plans)}. If the credit DID land, this account now holds those items twice. To reconcile: sum account_vault_log Count over Deposit rows for this account and class key in the incident window and compare against the account_vault_class.count delta - any guid named here that ALSO has a Deposit row is a dupe the player kept, and any that does not is a credit that never happened.");

                foreach (var index in group.ItemIndexes)
                    records[index].MarkUnknown(UnavailableMessage);

                return;
            }

            foreach (var index in group.ItemIndexes)
                records[index].MarkCredited();
        }

        /// <summary>
        /// Stores one classifiable item as a COUNT on a class row, destroying its biota.
        ///
        /// THE ORDER OF THE STEPS IS THE ITEM-LOSS MITIGATION and is not stylistic, exactly as it is in
        /// <see cref="TryDepositBatch"/>: the row is incremented FIRST and the biota is destroyed
        /// SECOND, because a dupe is recoverable from the audit log and a vanished item is not.
        ///
        /// THE ROUND-TRIP SELF-CHECK RUNS BEFORE ANYTHING IS DESTROYED, and it is the guarantee that
        /// makes destroying the biota defensible at all. TryDescribeClass proves the item differs from
        /// its template only on the whitelist; this proves the other half, that replaying the payload
        /// onto a fresh template really does produce an item of the SAME class. If it does not - a
        /// materializer that drops a field, a weenie that changed under the account, a dat that will
        /// not render - the item keeps its biota and goes to a vault container instead. Falling back
        /// costs one entry and nothing else; guessing costs the player their item.
        ///
        /// <paramref name="classKey"/> and <paramref name="overrides"/> come from the ONE
        /// TryDescribeClass call TryDeposit made outside every lock. Nothing here re-runs the predicate
        /// on the deposited item.
        /// </summary>
        private bool DepositToClass(WorldObject item, VaultActor actor, string classKey, VaultItemClassOverrides overrides,
                                    AccountVaultAction auditAction, out string failReason)
        {
            failReason = null;

            var wcid = item.WeenieClassId;
            var name = item.Name;

            // The amount the pool gains. Read off the item BEFORE anything is destroyed, and clamped at
            // zero because total_Value carries a non-negative guard in the database: a negative Value
            // would make the guarded UPDATE refuse the whole deposit rather than store a negative pool.
            var value = item.Value ?? 0;

            if (value < 0)
                value = 0;

            // VaultDepositPhase.RoundTrip. HOISTED out of the `if` so the phase closes BEFORE the fallback
            // into DepositToVault, which charges phases of its own: a scope still open across that call
            // would make this phase enclose them and double-count. ClassRoundTripHolds swallows its own
            // exceptions, so the region between Begin and End is straight-line either way.
            var roundTripScope = VaultDepositPhaseProfile.Begin();

            var roundTripHolds = ClassRoundTripHolds(wcid, overrides, classKey);

            VaultDepositPhaseProfile.End(VaultDepositPhase.RoundTrip, roundTripScope);

            if (!roundTripHolds)
            {
                log.Warn($"[VAULT] account {AccountId}: 0x{item.Guid.Full:X8} ({name}, wcid {wcid}) passed the class predicate but does NOT rebuild to its own class key, so it is being stored as an ordinary biota instead. Nothing was destroyed.");

                return DepositToVault(item, actor, out failReason);
            }

            var seed = new AccountVaultClassSeed
            {
                Wcid = wcid,
                ValueBandPct = VaultItemClass.ValueExcluded,
                CanonicalForm = VaultItemClass.CanonicalForm(wcid, overrides, VaultItemClass.ValueExcluded),
            };

            // Row FIRST. If this is REFUSED, nothing has happened and the player keeps the item.
            // Unknown across the call, exactly as the batched deposit marks its own credits.
            depositCommit = VaultDepositCommit.Unknown;

            // VaultDepositPhase.Upsert, the class branch's shard write. Straight-line between Begin and
            // End, so the End cannot be skipped by a return.
            var upsertScope = VaultDepositPhaseProfile.Begin();

            var credit = backend.TryAdjustAccountVaultClass(AccountId, classKey, 1, value, seed, out var newCount, out var newTotalValue);

            VaultDepositPhaseProfile.End(VaultDepositPhase.Upsert, upsertScope);

            if (credit == AccountVaultStackAdjustResult.Refused)
            {
                depositCommit = VaultDepositCommit.None;

                log.Error($"[VAULT] account {AccountId}: the class ledger refused a deposit of 0x{item.Guid.Full:X8} into class {classKey} (wcid {wcid}). The item was NOT destroyed.");
                failReason = UnavailableMessage;
                return false;
            }

            // The SAME deposit/withdraw asymmetry TryDepositBatch documents at length, applied to the
            // same unknown for the same reason: the item still exists in the player's hands, so
            // destroying it over a credit that may never have landed is a certain unrecoverable loss,
            // while keeping it when the credit DID land is a visible dupe an operator can reconcile
            // against account_vault_log. A visible dupe beats a silent loss.
            if (credit == AccountVaultStackAdjustResult.Failed)
            {
                log.Error($"[VAULT] CLASS STATE UNKNOWN: account {AccountId}, class {classKey}, wcid {wcid}, delta +1 item / +{value} value. A deposit credit neither committed nor provably failed. The item 0x{item.Guid.Full:X8} was NOT destroyed and the player keeps it - if the credit DID land, this account now holds that item twice and needs reconciling against account_vault_log.");

                InvalidateClasses($"an unknown-outcome deposit credit into class {classKey}");

                // Left at Unknown, for the reason TryDepositBatch's Failed arm gives.
                failReason = UnavailableMessage;
                return false;
            }

            depositCommit = VaultDepositCommit.Committed;

            if (credit == AccountVaultStackAdjustResult.AppliedCountUnknown)
            {
                // The item IS on the row; only the resulting count and total are unknown. Proceed with
                // the deposit exactly as for Applied, but RE-READ rather than writing numbers that
                // would be a guess - and a guessed total is worse than a guessed count here, because
                // the withdraw path divides by it.
                InvalidateClasses($"a deposit credit into class {classKey} whose count could not be read back");
            }
            else
            {
                // VaultDepositPhase.Apply, INCLUDING the stateLock acquisition, for the reason
                // VaultDepositPhase.Cap gives. Straight-line: nothing returns from inside.
                var applyScope = VaultDepositPhaseProfile.Begin();

                lock (stateLock)
                    ApplyClassCountLocked(classKey, wcid, seed.CanonicalForm, overrides, newCount, newTotalValue);

                VaultDepositPhaseProfile.End(VaultDepositPhase.Apply, applyScope);
            }

            // Bumped HERE, the moment the credit is committed, and not on TryDeposit's success return:
            // DestroyItem below can throw, and a throw unwinds past any bump placed after this call.
            Interlocked.Increment(ref version);

            // Destroy SECOND. The reverse order loses the item on a crash.
            var destroyScope = VaultDepositPhaseProfile.Begin();

            world.DestroyItem(item);

            VaultDepositPhaseProfile.End(VaultDepositPhase.Destroy, destroyScope);

            // auditAction rather than a hard-coded Deposit: a background FOLD writes
            // AccountVaultAction.Fold, so the migration stays separable from the player's own activity
            // in the same table an investigation reads - folding one large vault writes thousands of
            // rows in minutes. The value and the class key are what make a total_Value drift
            // reconstructable at all: wcid alone cannot say which of an account's several pools of the
            // same wcid this row moved value into.
            var auditScope = VaultDepositPhaseProfile.Begin();

            WriteLog(auditAction, actor, wcid, null, name, 1, value, classKey);

            VaultDepositPhaseProfile.End(VaultDepositPhase.Audit, auditScope);

            return true;
        }

        /// <summary>
        /// Whether a fresh instance built from this payload really is an item of the SAME class.
        ///
        /// Materialize, re-run the predicate on the result, re-derive the key, compare. Anything short
        /// of an exact match is a false - a null from the materializer, a probe the predicate now
        /// refuses, a key that differs by one character. The probe is destroyed either way, because it
        /// is scratch and holds a dynamic guid.
        ///
        /// It is a real object rather than a paper proof deliberately: the interesting failures are the
        /// ones that only exist once the object has been built and rendered (a dat-derived icon, a
        /// property the weenie carries that the payload does not overwrite), and none of them is
        /// visible from the payload alone.
        /// </summary>
        private bool ClassRoundTripHolds(uint wcid, VaultItemClassOverrides overrides, string classKey)
        {
            WorldObject probe = null;

            try
            {
                // pooledValue NULL, so the payload's own Value is replayed. A pooled share would change
                // Value on purpose, and this check is about whether everything ELSE survives the trip -
                // at band 0 the key excludes Value anyway, so supplying one would test nothing and
                // would make the check depend on the row's arithmetic.
                probe = world.MaterializeClass(wcid, overrides, null);

                if (probe == null)
                {
                    log.Error($"[VAULT] account {AccountId}: the class round-trip self-check could not materialize wcid {wcid} for class {classKey}.");
                    return false;
                }

                if (!world.TryDescribeClass(probe, out var rebuilt))
                {
                    log.Error($"[VAULT] account {AccountId}: the class round-trip self-check rebuilt wcid {wcid} but the predicate then refused the result, so class {classKey} is not provably reversible.");
                    return false;
                }

                var rebuiltKey = VaultItemClass.ClassKey(wcid, rebuilt, VaultItemClass.ValueExcluded);

                if (!string.Equals(rebuiltKey, classKey, StringComparison.Ordinal))
                {
                    log.Error($"[VAULT] account {AccountId}: the class round-trip self-check rebuilt wcid {wcid} into class {rebuiltKey}, not {classKey}. The item is NOT interchangeable with its own class and must keep its biota.");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                log.Error($"[VAULT] account {AccountId}: the class round-trip self-check threw for wcid {wcid} / class {classKey}; treating the item as unclassifiable so it keeps its biota.", ex);
                return false;
            }
            finally
            {
                if (probe != null)
                {
                    try
                    {
                        world.DestroyItem(probe);
                    }
                    catch (Exception destroyEx)
                    {
                        log.Error($"[VAULT] account {AccountId}: could not destroy the class round-trip probe for wcid {wcid}; a guid has been leaked.", destroyEx);
                    }
                }
            }
        }

        /// <summary>
        /// Writes a class row's fresh count and total into <see cref="classes"/>, or removes it once the
        /// count reaches zero. Caller holds <see cref="stateLock"/>.
        ///
        /// A row at zero is REMOVED rather than kept at zero, for the same reason
        /// <see cref="ledger"/> keeps only non-zero rows: <see cref="EntryCountLocked"/> counts what is
        /// in this dictionary, so a zero row left behind would eat a capacity entry over a holding the
        /// panel does not draw.
        /// </summary>
        private void ApplyClassCountLocked(string classKey, uint wcid, string canonicalForm, VaultItemClassOverrides overrides, long newCount, long newTotalValue)
        {
            // Beside every mutation below, and unconditional: a count change alone does not move a row
            // between display groups, but a REMOVAL and an INSERTION both do, and the memo carries the
            // VaultClassRow objects themselves so a stale one would hand a reader a removed row.
            InvalidateClassGroupingLocked();

            if (newCount <= 0)
            {
                classes.Remove(classKey);
                return;
            }

            if (classes.TryGetValue(classKey, out var existing))
            {
                existing.Count = newCount;
                existing.TotalValue = newTotalValue;
                return;
            }

            classes[classKey] = new VaultClassRow
            {
                ClassKey = classKey,
                Wcid = wcid,
                Count = newCount,
                TotalValue = newTotalValue,
                CanonicalForm = canonicalForm,
                Overrides = overrides,
            };
        }

        /// <summary>
        /// The class key this item would be stored under AND the display group that class draws in, or
        /// false when it is not classifiable or the counted tier is switched off.
        ///
        /// BOTH KEYS FROM ONE PREDICATE CALL, deliberately. The cap rule needs the display key as well
        /// as the class key (see <see cref="AddsEntryLocked"/>), and they are derived from the same
        /// override payload, so asking twice would double the most expensive call on the deposit path
        /// for no new information.
        ///
        /// MUST BE CALLED FROM OUTSIDE EVERY LOCK. TryDescribeClass takes the item's own
        /// BiotaDatabaseLock read lock, and BiotaDatabaseLock is NoRecursion, so a caller already
        /// holding it gets a silent false on every call.
        /// </summary>
        private bool TryDescribeClassKeys(WorldObject item, out string classKey, out string classDisplayKey)
        {
            classKey = null;
            classDisplayKey = null;

            if (!ClassStorageEnabled)
                return false;

            if (!world.TryDescribeClass(item, out var overrides))
                return false;

            classKey = VaultItemClass.ClassKey(item.WeenieClassId, overrides, VaultItemClass.ValueExcluded);
            classDisplayKey = VaultCollapse.ClassDisplayGroupKey(item.WeenieClassId, overrides);

            return true;
        }

        /// <summary>
        /// One item's share of a class row's pooled Value: round(remaining total / remaining count),
        /// in exact INTEGER arithmetic.
        ///
        /// INTEGER ON PURPOSE, the same ruling VaultItemClass.ValueBand makes for the same reason: a
        /// double divide would round differently on the last bit between runtimes, and here that error
        /// would not merely bucket an item differently - it would leave a remainder stranded on the row
        /// after a full withdraw, so the pool would stop being conserved. <c>(t + c/2) / c</c> is
        /// round-half-up for non-negative t and positive c, and because the caller decrements BOTH the
        /// remaining total and the remaining count by what it just handed out, the last item of a full
        /// withdraw takes exactly whatever is left. The sum of a full withdraw therefore equals
        /// total_Value exactly, with no reconciliation step anywhere.
        ///
        /// The int clamp cannot fire in practice - a total is a sum of int Values over that many items,
        /// so the share is at most int.MaxValue - and is there because PropertyInt.Value is an int and
        /// a silent overflow is worse than a clamped one.
        /// </summary>
        internal static int PooledShare(long remainingTotal, long remainingCount)
        {
            if (remainingCount <= 0 || remainingTotal <= 0)
                return 0;

            var share = (remainingTotal + (remainingCount / 2)) / remainingCount;

            return share > int.MaxValue ? int.MaxValue : (int)share;
        }

        /// <summary>
        /// account_vault_class_storage. The master switch for the counted tier: off, nothing new
        /// collapses into a class row and nothing folds, while rows that already exist stay readable
        /// and withdrawable. It is a STOP, never a rollback.
        /// </summary>
        public static bool ClassStorageEnabled => PropertyManager.GetBool("account_vault_class_storage").Item;

        /// <summary>The audit-row actor name for a fold. Not a player; an operator reading account_vault_log needs to see that at a glance.</summary>
        private const string FoldActorName = "(vault fold)";

        /// <summary>
        /// Stored-biota guids the class predicate has already REFUSED in this store's lifetime.
        ///
        /// This is what keeps the fold from costing anything in the steady state. Without it every pass
        /// would re-run the predicate over the same unfoldable items forever, and the predicate is the
        /// most expensive thing on the path. It is not persisted, and must not be: a weenie edit or a
        /// new build can change what is classifiable, and a store is rebuilt from the shard often
        /// enough (idle eviction is ten minutes) that the set re-derives itself for free.
        ///
        /// Guarded by <see cref="stateLock"/>.
        /// </summary>
        private readonly HashSet<uint> foldRefused = new HashSet<uint>();

        /// <summary>
        /// Set when a fold pass found no stored biota it had not already refused, so there is nothing
        /// left to try. Cleared whenever a new stored biota appears or the store reloads.
        ///
        /// Guarded by <see cref="stateLock"/>.
        /// </summary>
        private bool foldExhausted;

        /// <summary>
        /// Stored-biota guid -> the class key the predicate last derived for it. A HINT for candidate
        /// SELECTION only; nothing destructive is ever done on the strength of an entry here, because
        /// the fold re-derives the key from the live item before it touches anything.
        ///
        /// It is what makes class-ordered folding affordable. The class of a stored item cannot be
        /// known without running the predicate, and the predicate must be called outside every lock,
        /// so the selection step under <see cref="stateLock"/> cannot ask "which of these are class
        /// X". Remembering each answer as it is computed lets the NEXT pass fill its whole budget from
        /// one class with no extra predicate calls at all.
        ///
        /// Entries are removed as soon as they stop being useful - when the item folds (its guid is
        /// gone), when it is refused (<see cref="foldRefused"/> covers it from then on), and when it
        /// LEAVES A VAULT by any route at all (see <see cref="ForgetFoldMemoryLocked"/>) - so this
        /// cannot grow past the number of stored biotas whose class is known but not yet drained.
        /// Dropped wholesale with <see cref="foldRefused"/> on a reload, for the same reason.
        ///
        /// Guarded by <see cref="stateLock"/>.
        /// </summary>
        private readonly Dictionary<uint, string> foldClassHints = new Dictionary<uint, string>();

        /// <summary>
        /// The class the fold is currently DRAINING, or null when it is between classes.
        ///
        /// THE OWNER'S RULING: a class that is partly folded is finished before another is started. A
        /// partly folded class is visible to the player as two rows that are not adjacent and that show
        /// different values - the group row shows its representative's own Value, the class row shows
        /// the pooled average - so the migration is least confusing when at most ONE such pair exists
        /// at a time. This does NOT eliminate the split for a class larger than the per-pass budget,
        /// and is not meant to: it bounds the vault to one split pair instead of many scattered ones,
        /// and makes the transient legible as one row shrinking while another grows.
        ///
        /// It is the FOLD's concern alone. Nothing on the deposit path reads it, and the deposit stays
        /// O(1).
        ///
        /// Guarded by <see cref="stateLock"/>.
        /// </summary>
        private string foldTargetClassKey;

        /// <summary>
        /// The guids the fold currently remembers anything about, from either map. Test instrumentation
        /// on the same seam as <see cref="GroupingRebuilds"/>, and for the same reason: nothing else can
        /// observe it, and the property under test is an ABSENCE - that a guid no longer in any vault is
        /// no longer in either map. An absence is invisible to every functional assertion.
        /// </summary>
        internal HashSet<uint> FoldRememberedGuidsForTest
        {
            get
            {
                lock (stateLock)
                {
                    var remembered = new HashSet<uint>(foldRefused);

                    foreach (var guid in foldClassHints.Keys)
                        remembered.Add(guid);

                    return remembered;
                }
            }
        }

        /// <summary>
        /// Whether this store's fold has LATCHED "nothing left to try" - <see cref="foldExhausted"/>.
        ///
        /// It is the store's own answer to "is this account migrated", and it is deliberately the only
        /// thing outside this class may ask: a caller must never infer completion from "the last pass
        /// folded nothing", because a pass folds nothing for several reasons that are not completion at
        /// all (a cold store, the switch off, a zero budget, a ledger abort, a window that happened to
        /// hold only items of another class). See VaultFoldMigrationReport, which exists because that
        /// exact inference once declared a migration finished over two vaults that had folded zero.
        /// </summary>
        internal bool FoldHasNothingLeft
        {
            get { lock (stateLock) return foldExhausted; }
        }

        /// <summary>
        /// Folds up to <paramref name="budget"/> already-stored biotas into counted class rows, returns
        /// how many actually folded, and reports the pass's own outcome in <paramref name="result"/>.
        ///
        /// THIS IS THE ONLY WAY A FOLD HAPPENS, and AccountVaultFoldMigration - the engine behind
        /// /vaultclassfold - is its only production caller. A background rotation on the world heartbeat
        /// used to drive a budget-free overload of this method one store per second; it was removed once
        /// the migration it existed to perform had finished on prod (2026-09-26), because from then on it
        /// was permanent world-loop cost for work that cannot come back. The budget is therefore the
        /// CALLER's batch size rather than a tunable, and the outcome is reported rather than inferred:
        /// zero folded is the same number for "nothing is left", "this store is cold", "the ledger is
        /// refusing" and "the switch is off", and treating those as the same answer is precisely how a
        /// run reports COMPLETE over an unmigrated vault.
        ///
        /// WHICH THREAD THIS RUNS ON, because it is the question that decides whether this is safe.
        /// It runs on the store's mutation queue, and <see cref="Enqueue(Action)"/> drains queued work on
        /// the CALLING thread - so it runs on whichever thread enqueued it. AccountVaultFoldMigration
        /// enqueues from its own background worker, so a migration batch is NOT world-loop work.
        ///
        /// A BACKGROUND THREAD IS AVAILABLE, and an earlier version of this comment claimed otherwise -
        /// that moving the fold off the world loop "would mean creating and destroying WorldObjects off
        /// the world loop, which nothing else in this subsystem does". That is false, and the
        /// counter-example is in the same namespace: AccountVaultBarrelReaper runs its own background
        /// thread (AccountVaultBarrelReaper.cs:216) and destroys vault biotas from it through this very
        /// queue - AccountVaultBarrelReaper.cs:160 enqueues <see cref="TryPurgeFromBarrel"/>, which
        /// calls world.DestroyItem, which is WorldObject.Destroy(). For a vault item every branch of
        /// Destroy() that touches live world state is inert (it is not a Container, Creature, Pet,
        /// Vendor, PersonalVendor or SpellProjectile; NotifyOfEvent returns immediately with no
        /// GeneratorId; IsGenerator is false with no GeneratorProfiles; and a vault item is never in a
        /// landblock, which is what the spawn filter exists to guarantee, so CurrentLandblock is null),
        /// and what is left is RemoveBiotaFromDatabase -> DatabaseManager.Shard.RemoveBiota, which is
        /// the serialized shard queue and is thread-safe by construction. GuidManager's dynamic
        /// allocator holds lock (this) for the whole of both Alloc (GuidManager.cs:202) and Recycle
        /// (:292), so the probe the round-trip check builds is safe off the loop too.
        ///
        /// AND IT NEVER STARTS A LOAD. The readiness gate below is <see cref="IsWarmLocked"/>, not
        /// <see cref="CheckReadyLocked"/>, so a store that is registered but cold is skipped for free -
        /// no shard read, no error line, no tick cost. See the comment on that gate for why the
        /// obvious-looking ready check is the wrong one here.
        ///
        /// IT IS IDEMPOTENT. A folded item is destroyed, so it is gone from the vault and cannot be
        /// folded twice; an item the predicate refuses is remembered in <see cref="foldRefused"/> and
        /// never retried, and is never touched destructively in the first place. A second pass over a
        /// fully folded store therefore folds nothing and does no database work at all.
        ///
        /// IT FOLDS ONE CLASS AT A TIME. See <see cref="foldTargetClassKey"/> for the ruling and what
        /// it does and does not buy. The cost of the ordering is bounded: at most two walks of the
        /// stored items under <see cref="stateLock"/> instead of one - the same ORDER, and nothing per
        /// item beyond a dictionary lookup - and never more than <paramref name="budget"/> predicate
        /// calls. A pass under the ordering does strictly LESS destructive work than one without it,
        /// because items of other classes are recognised and skipped before anything is taken out of a
        /// vault.
        ///
        /// <paramref name="currentUnixTime"/> is only used to rate-limit
        /// <see cref="VaultFoldProfile"/>'s slow-pass line.
        ///
        /// <paramref name="result"/> is always non-null on return, on every path including a throw-free
        /// early refusal, and its <see cref="VaultFoldBatchResult.Terminal"/> is always set exactly once.
        ///
        /// HOW TO CALL IT, because the obvious spelling does not compile. This must run on the mutation
        /// queue, so the call goes inside an <see cref="Enqueue(Action)"/> lambda - and C# forbids
        /// capturing the ENCLOSING method's own out/ref parameter in a lambda. So this is a compile error:
        /// <code>
        /// // WRONG: `mine` is the caller's own out parameter
        /// void Caller(out VaultFoldBatchResult mine) =&gt; store.Enqueue(() =&gt; store.FoldSomeStoredItemsOnQueue(now, 25, out mine));
        /// </code>
        /// while passing a captured LOCAL is fine, because a captured local is hoisted to a closure field
        /// and a field may be an out argument:
        /// <code>
        /// VaultFoldBatchResult captured = null;
        /// store.Enqueue(() =&gt; store.FoldSomeStoredItemsOnQueue(now, 25, out captured));
        /// // `captured` is readable here; Enqueue does not return until the work has run.
        /// </code>
        /// It is also why <see cref="VaultFoldBatchResult"/> is a class: a struct would be copied at the
        /// capture and the counters would arrive back at zero.
        /// </summary>
        internal int FoldSomeStoredItemsOnQueue(double currentUnixTime, int budget, out VaultFoldBatchResult result)
        {
            AssertOnMutationQueue(nameof(FoldSomeStoredItemsOnQueue));

            // Built first so no return path below can leave it null, and defaulted to MoreToDo rather
            // than to Exhausted: a terminal this method forgot to set must err towards "keep going",
            // which costs a wasted pass, never towards "finished", which would report a migration
            // complete that is not.
            result = new VaultFoldBatchResult();

            if (!ClassStorageEnabled)
            {
                result.Terminal = VaultFoldBatchTerminal.StorageDisabled;
                return 0;
            }

            if (budget <= 0)
            {
                result.Terminal = VaultFoldBatchTerminal.BudgetZero;
                return 0;
            }

            // Started BEFORE stateLock is taken, because the candidate scan under that lock walks every
            // stored biota and is part of what a pass costs. See VaultFoldProfile for why this is timed
            // directly instead of being left to the slow-tick capture.
            var timer = Stopwatch.StartNew();

            List<WorldObject> candidates;
            string target;

            lock (stateLock)
            {
                if (foldExhausted)
                {
                    result.Terminal = VaultFoldBatchTerminal.Exhausted;
                    VaultFoldProfile.RecordIdlePass();
                    return 0;
                }

                // Gated on WARM, not on READY, and the difference is the whole point of this line.
                //
                // CheckReadyLocked - what every player-facing mutation uses - runs
                // TryEnsureLoadedLocked, which on a COLD store performs the load: the vault index, the
                // stack ledger, the class ledger and one biota read per vault, synchronously, under
                // stateLock. That is correct for a player who is asking for their vault and is waiting
                // for the answer. It is wrong here, because the fold is nobody's request: it is
                // migration work, and it must never be the thing that causes a cold vault to load.
                //
                // And a cold store IS reachable from here. A caller walks accounts, not attached
                // players, so the set it reaches is not the same as "stores with a player attached" - a
                // store can be registered and never loaded (the /mule search all path builds stores and
                // warms them later, on purpose, through the rate-limited warm queue). Folding through
                // CheckReadyLocked would load those for accounts nobody is using; on a shard whose
                // account_vault table is unreachable it additionally logged an ERROR per pass.
                //
                // Skipping a cold store costs the migration nothing: it holds no stored biotas in
                // memory to fold, and AccountVaultFoldMigration reports NotWarm rather than treating the
                // zero as completion. IsWarmLocked is strictly stronger than CheckReadyLocked (see its
                // doc), so nothing below this line is less guarded than it was.
                if (!IsWarmLocked())
                {
                    result.Terminal = VaultFoldBatchTerminal.NotWarm;
                    VaultFoldProfile.RecordIdlePass();
                    return 0;
                }

                target = foldTargetClassKey;
                candidates = new List<WorldObject>();

                if (target == null)
                {
                    // Between classes. Take the first budget non-refused items in stored order and let
                    // the first one that proves foldable decide which class this pass drains.
                    TakeFoldCandidatesLocked(candidates, budget, hint => true);
                }
                else
                {
                    // Draining one class. Items already KNOWN to be of it first, so a pass costs no
                    // predicate call it does not have to make...
                    TakeFoldCandidatesLocked(candidates, budget, hint => string.Equals(hint, target, StringComparison.Ordinal));

                    // ...then unknowns, to keep discovering more of the same class beyond the window
                    // the first pass happened to see. Without this second phase a class larger than one
                    // window would be abandoned half folded the moment its known items ran out, which
                    // is the exact state the target exists to avoid.
                    if (candidates.Count < budget)
                        TakeFoldCandidatesLocked(candidates, budget, hint => hint == null);
                }

                if (candidates.Count == 0)
                {
                    if (target != null)
                    {
                        // No item is known to be of the target class and none is unknown, so as far as
                        // this store can tell the class is drained. Release it; the next pass picks a
                        // new one from what is left. NOT foldExhausted: there may be plenty left that
                        // is simply hinted to other classes.
                        //
                        // MoreToDo for exactly that reason. This is the one pass that does no work and
                        // is still not finished, and calling it Exhausted here would stop an operator's
                        // migration one class in.
                        foldTargetClassKey = null;
                        result.Terminal = VaultFoldBatchTerminal.MoreToDo;
                        VaultFoldProfile.RecordIdlePass();
                        return 0;
                    }

                    // Every stored biota has already been refused once, so there is nothing left to
                    // try until something new arrives or the store reloads.
                    foldExhausted = true;
                    result.Terminal = VaultFoldBatchTerminal.Exhausted;
                    VaultFoldProfile.RecordIdlePass();
                    return 0;
                }
            }

            var actor = new VaultActor(AccountId, 0, FoldActorName);
            var folded = 0;

            // Counted in the loop rather than taken from candidates.Count, because the loop can BREAK
            // early on a database problem. Reporting the whole budget as examined after a two-item
            // pass would make ms-per-item look far better than it is.
            var examined = 0;

            var aborted = false;

            try
            {
                foreach (var item in candidates)
                {
                    examined++;

                    var outcome = FoldOne(item, actor, ref target);

                    switch (outcome)
                    {
                        case FoldOutcome.Folded:
                            folded++;
                            break;

                        case FoldOutcome.RefusedPredicate:
                            result.RefusedPredicate++;
                            break;

                        case FoldOutcome.RefusedRoundTrip:
                            result.RefusedRoundTrip++;
                            break;

                        case FoldOutcome.RefusedTakeOut:
                            result.RefusedTakeOut++;
                            break;

                        case FoldOutcome.WrongClass:
                            result.WrongClass++;
                            break;
                    }

                    // Stop the pass. The ledger is refusing or unreachable, and grinding through the
                    // rest of the budget against it would multiply one database problem by the budget.
                    //
                    // COUNTED IN ITS OWN BUCKET, not left out of all of them. This candidate was already
                    // added to `examined` above, so leaving it uncounted made the outcome buckets fall
                    // one short of Examined with no field naming the gap - see
                    // VaultFoldBatchResult.Aborted. Incremented here rather than in the switch because
                    // this arm also ends the loop, and keeping the two facts together is what stops one
                    // being added without the other.
                    if (outcome == FoldOutcome.AbortPass)
                    {
                        result.Aborted++;
                        aborted = true;
                        break;
                    }
                }

                lock (stateLock)
                {
                    // A window that yielded nothing of the target class means the class is finished as
                    // far as this store can see, so the target is released HERE rather than costing a
                    // whole further pass to discover. Clearing it wrongly is harmless - the next pass
                    // simply re-adopts the same class - while holding it wrongly stalls the migration.
                    foldTargetClassKey = folded == 0 && examined > 0 ? null : target;
                }
            }
            finally
            {
                // In a finally so a throw from anywhere in the loop still reports what the pass cost
                // and how far it got. An unreported expensive pass is exactly the blindness this
                // instrumentation exists to remove.
                VaultFoldProfile.RecordPass(AccountId, timer.Elapsed.TotalMilliseconds, examined, folded, currentUnixTime);

                // Also in the finally, and for the same reason: a caller that catches a throw from the
                // loop still gets an accurate count of what the pass managed before it died. Aborted
                // rather than MoreToDo when the ledger stopped the pass, because a caller draining an
                // account in a loop must not spin against a refusing ledger - see
                // AccountVaultFoldMigration, which stops that account and says so.
                result.Examined = examined;
                result.Folded = folded;
                result.Terminal = aborted ? VaultFoldBatchTerminal.Aborted : VaultFoldBatchTerminal.MoreToDo;
            }

            return folded;
        }

        /// <summary>
        /// Appends stored biotas whose class HINT satisfies <paramref name="accept"/> to
        /// <paramref name="candidates"/>, skipping anything already refused or already in the list,
        /// until the list reaches <paramref name="budget"/>. Caller holds <see cref="stateLock"/>.
        ///
        /// The hint handed to <paramref name="accept"/> is null when this store has never derived a
        /// class for that item. One walk of the stored items per call, so the two-phase selection above
        /// is two walks - the same ORDER as the single walk it replaced, and still nothing per stored
        /// item beyond a dictionary lookup.
        /// </summary>
        /// <summary>
        /// Drops everything the fold remembers about one guid. Caller holds <see cref="stateLock"/>.
        ///
        /// CALL THIS WHEREVER AN ITEM CROSSES A VAULT BOUNDARY, in either direction. Both maps are
        /// keyed by guid and both would otherwise outlive the item they describe: a player withdrawing
        /// a hinted item, a barreling, a group withdraw and the fold's own take-out all remove an item
        /// from a vault without anything else noticing, and only a class RELOAD clears the maps
        /// wholesale. Without this the maps grow by one entry per withdrawal until the next reload, and
        /// the bound both doc comments state would be false.
        ///
        /// Clearing on the way IN matters too, and not only for symmetry: an item that left the vault
        /// and came back may have been changed while it was out, so a refusal or a hint derived before
        /// it left is an answer about a different item. Forgetting costs one predicate call later and
        /// is the only answer that cannot be stale.
        ///
        /// It is NOT folded into <see cref="InvalidateGroupingLocked"/>, which would otherwise be the
        /// obvious single place: that method is called by every vault mutation INCLUDING every deposit,
        /// and it is not told which guid moved, so pruning there would mean walking the maps against
        /// the stored items on every deposit - per-deposit work proportional to vault size, which is
        /// exactly what the class tier's O(1) deposit forbids.
        /// </summary>
        private void ForgetFoldMemoryLocked(uint guid)
        {
            foldRefused.Remove(guid);
            foldClassHints.Remove(guid);
        }

        private void TakeFoldCandidatesLocked(List<WorldObject> candidates, int budget, Func<string, bool> accept)
        {
            if (candidates.Count >= budget)
                return;

            foreach (var item in EnumerateStoredItemsLocked())
            {
                if (item == null || foldRefused.Contains(item.Guid.Full))
                    continue;

                if (candidates.Contains(item))
                    continue;

                foldClassHints.TryGetValue(item.Guid.Full, out var hint);

                if (!accept(hint))
                    continue;

                candidates.Add(item);

                if (candidates.Count >= budget)
                    return;
            }
        }

        /// <summary>
        /// What one candidate did. <see cref="AbortPass"/> is the case a bool cannot express: it is not
        /// "this item did not fold", it is "stop folding".
        /// </summary>
        private enum FoldOutcome
        {
            /// <summary>
            /// The class PREDICATE refused: this item is not classifiable at all. The item keeps its
            /// biota and is remembered as refused.
            ///
            /// The three refusals below were one member until the batch seam needed to report them
            /// apart. They are all still "the item keeps its biota and is never retried" - the split
            /// exists so an operator reading a migration report can tell an unclassifiable item (normal,
            /// expected, most of a real vault) from a class that cannot be rebuilt (a content bug) from
            /// a take-out that failed (a store problem).
            /// </summary>
            RefusedPredicate,

            /// <summary>The round-trip self-check refused: the class cannot be rebuilt into the same class.</summary>
            RefusedRoundTrip,

            /// <summary>The item could not be taken out of its vault, so nothing was folded.</summary>
            RefusedTakeOut,

            /// <summary>The item collapsed into a class row and its biota was destroyed.</summary>
            Folded,

            /// <summary>The class ledger refused or could not be reached. The item was put back; the whole pass stops.</summary>
            AbortPass,

            /// <summary>
            /// The item is foldable but belongs to a DIFFERENT class than the one this pass is
            /// draining. Nothing was touched; its class is now remembered, so a later pass can select
            /// it without paying for the predicate again.
            /// </summary>
            WrongClass,
        }

        /// <summary>
        /// One candidate. Split out of
        /// <see cref="FoldSomeStoredItemsOnQueue(double, int, out VaultFoldBatchResult)"/> so the pass's
        /// timing wrapper is one readable block rather than a try/finally wrapped around forty lines of
        /// branching.
        ///
        /// <paramref name="target"/> is the class this pass is draining. Null means the pass has not
        /// chosen one yet, and the FIRST item that proves fully foldable adopts it - after the
        /// round-trip self-check, never before, so a class that cannot actually be rebuilt is never
        /// adopted and cannot stall the migration behind itself.
        /// </summary>
        private FoldOutcome FoldOne(WorldObject item, VaultActor actor, ref string target)
        {
            // OUTSIDE stateLock, for the reason TryDeposit's own remark gives: TryDescribeClass takes
            // the item's own BiotaDatabaseLock read lock and that lock is NoRecursion.
            if (!world.TryDescribeClass(item, out var overrides))
            {
                lock (stateLock)
                {
                    foldRefused.Add(item.Guid.Full);
                    foldClassHints.Remove(item.Guid.Full);
                }

                return FoldOutcome.RefusedPredicate;
            }

            var classKey = VaultItemClass.ClassKey(item.WeenieClassId, overrides, VaultItemClass.ValueExcluded);

            // Remembered the moment it is known, so the next pass can SELECT this item by class without
            // running the predicate again. It is a selection hint and never a licence to act: the key
            // used below is this freshly derived one, not anything read back out of the map.
            lock (stateLock)
                foldClassHints[item.Guid.Full] = classKey;

            // The owner's ruling, enforced here: one class is drained before another is started. An
            // item of a different class is left exactly where it is - not refused, not touched - and
            // its hint above is what makes skipping it cheap from now on.
            if (target != null && !string.Equals(classKey, target, StringComparison.Ordinal))
                return FoldOutcome.WrongClass;

            // The round-trip check is run HERE, before the item is taken out of its vault, and not left
            // to DepositToClass. DepositToClass answers a failed check by falling back to
            // DepositToVault, which puts the item back and reports SUCCESS - correct for a real
            // deposit, but for a fold it would mean this item is re-tried on every future pass forever.
            // Checking first lets the refusal be REMEMBERED. It is the same one implementation called
            // twice, never a second copy of the rule; a foldable item pays for it once, in a background
            // migration.
            if (!ClassRoundTripHolds(item.WeenieClassId, overrides, classKey))
            {
                lock (stateLock)
                {
                    foldRefused.Add(item.Guid.Full);
                    foldClassHints.Remove(item.Guid.Full);
                }

                return FoldOutcome.RefusedRoundTrip;
            }

            // Adopted only once the class has PROVED foldable. Adopting on the predicate alone would
            // let a class that cannot be rebuilt become the target, and every later item of every other
            // class would then be skipped as WrongClass behind a class that can never drain.
            if (target == null)
                target = classKey;

            if (!TryTakeStoredItemForFold(item))
            {
                lock (stateLock)
                {
                    foldRefused.Add(item.Guid.Full);
                    foldClassHints.Remove(item.Guid.Full);
                }

                return FoldOutcome.RefusedTakeOut;
            }

            if (DepositToClass(item, actor, classKey, overrides, AccountVaultAction.Fold, out _))
            {
                // The biota is destroyed, so the guid is gone. Leaving a hint behind would be a slow
                // leak and, worse, a stale answer if that dynamic guid is ever handed out again.
                lock (stateLock)
                    foldClassHints.Remove(item.Guid.Full);

                return FoldOutcome.Folded;
            }

            // The class row refused the credit, or its outcome is unknown. Either way DepositToClass
            // did NOT destroy the item, so it goes straight back where it came from - never destroyed,
            // never dropped.
            if (!TryReturnWithdrawn(item, actor))
                log.Error($"[VAULT] ORPHAN: account {AccountId}: 0x{item.Guid.Full:X8} ({item.Name}) was taken out of its vault for a fold, the class credit did not land, and it could not be put back. It has NOT been destroyed and needs manual recovery.");

            lock (stateLock)
            {
                foldRefused.Add(item.Guid.Full);
                foldClassHints.Remove(item.Guid.Full);
            }

            return FoldOutcome.AbortPass;
        }

        /// <summary>
        /// Removes one stored biota from whichever of this account's vaults holds it, so the fold can
        /// hand it to <see cref="DepositToClass"/>.
        ///
        /// It is <see cref="WithdrawItem"/> without the audit row and without the access check: the
        /// item is not leaving the account, and the fold is not an actor. Everything else is the same,
        /// including saving the CONTAINER and not the item - the item's own row is left pointing at the
        /// vault, which is the recoverable direction if the fold dies before the item is destroyed.
        /// </summary>
        private bool TryTakeStoredItemForFold(WorldObject item)
        {
            Container vault = null;

            lock (stateLock)
            {
                foreach (var candidate in vaults)
                {
                    if (candidate.Inventory.ContainsKey(item.Guid))
                    {
                        vault = candidate;
                        break;
                    }
                }

                if (vault == null || !vault.TryRemoveFromInventory(item.Guid, out _))
                    return false;

                // Inside the lock, beside the removal - see groupingMemo's remarks.
                InvalidateGroupingLocked();
                ForgetFoldMemoryLocked(item.Guid.Full);
            }

            world.SaveBiota(vault);

            return true;
        }

        private bool DepositToVault(WorldObject item, VaultActor actor, out string failReason)
        {
            failReason = null;

            // VaultDepositPhase.FindVault, the one phase with no counterpart on either collapsing branch:
            // the free-slot scan, and the container CREATE when no vault has room.
            var findVaultScope = VaultDepositPhaseProfile.Begin();

            var vault = FindOrCreateVault();

            VaultDepositPhaseProfile.End(VaultDepositPhase.FindVault, findVaultScope);

            if (vault == null)
            {
                failReason = UnavailableMessage;
                return false;
            }

            // VaultDepositPhase.Apply, the vault branch's in-memory mutation, INCLUDING the stateLock
            // acquisition for the reason VaultDepositPhase.Cap gives. try/finally rather than a
            // straight-line End because the block returns from inside when TryAddToInventory refuses.
            var applyScope = VaultDepositPhaseProfile.Begin();

            try
            {
                lock (stateLock)
                {
                    // APPENDED at the end, rather than at Container.TryAddToInventory's default placement
                    // position of 0. At 0, Container.cs:566-570 renumbers EVERY existing item in the
                    // container - up to 255 of them - and each PlacementPosition write is a persisted
                    // property setter that takes that item's BiotaDatabaseLock. A 512-item sale paid that
                    // 512 times over a container that grows as it goes.
                    //
                    // Inventory.Count is the right index because a vault holds no side-pack items, so it
                    // equals the non-UseBackpackSlot count that Container.TryAddToInventory renumbers
                    // against. Two independent guards make that true: TryDeposit refuses a UseBackpackSlot
                    // item outright (see its own remark), and CreateVault pins ContainerCapacity to 0
                    // (:2439), which makes Container.TryAddToInventory:517 refuse one regardless of caller.
                    // FindVaultWithFreeSlotLocked has also already established Inventory.Count is below
                    // ItemCapacity, so the position cannot exceed 254.
                    //
                    // Positions do not have to be dense for this to be safe: Container's own load path
                    // normalises them to 0..n-1 by sorted order (Container.cs:176-181).
                    if (!vault.TryAddToInventory(item, placementPosition: vault.Inventory.Count))
                    {
                        log.Error($"[VAULT] account {AccountId}: TryAddToInventory refused 0x{item.Guid.Full:X8} on vault 0x{vault.Guid.Full:X8} ({vault.Inventory.Count} of {vault.ItemCapacity} slots used).");
                        failReason = UnavailableMessage;
                        return false;
                    }

                    // The biota is in this account's vault container from here on; the saves below can
                    // throw, and a caller reading LastDepositCommit must then see it as landed.
                    depositCommit = VaultDepositCommit.Committed;

                    // INSIDE the lock, beside the mutation, not after it - a reader taking stateLock between
                    // the two would otherwise see this item in the vault while the memo still described the
                    // vault without it.
                    //
                    // MAINTAINED, not dropped. Dropping it made a bulk deposit quadratic: each deposit
                    // invalidated the memo, so the next one re-walked every stored biota to answer the cap
                    // check, and an N-item sale into a V-item vault did O(N*V) work on the world thread
                    // inside the Sell handler. This places the one new item instead. The version bump that
                    // used to sit below the lock moved INTO this call for the same reason - see its remarks.
                    AddStoredItemToGroupingLocked(item);

                    // Marked here, saved once when the drain empties (Drain -> FlushDirtyVaultSaves). See
                    // dirtyVaults for why deferring the CONTAINER's save cannot lose an item: the item's
                    // own save below is what records its new home, and this row carries only the
                    // container's derived aggregates.
                    dirtyVaults.Add(vault);
                }
            }
            finally
            {
                VaultDepositPhaseProfile.End(VaultDepositPhase.Apply, applyScope);
            }

            // The version bump used to be HERE. It is now inside the locked block above, in
            // AddStoredItemToGroupingLocked, and it still satisfies the property this comment used to
            // state: it happens the moment the item is physically in the vault and not on TryDeposit's
            // success return, so a throw from anything below cannot unwind past it. Collocating it with
            // the mutation under stateLock is what lets the grouping memo be maintained rather than
            // dropped, because the memo is keyed on that stamp.

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

            // VaultDepositPhase.Upsert, the vault branch's shard write. The same phase the two collapsing
            // branches charge their row credit to, because it answers the same question - what the
            // persistence cost - and a tick may run either branch. This one ENQUEUES the save rather than
            // waiting on it: the callback arrives later on the database worker thread and is not measured
            // by any phase, which is correct, because no part of it is on the world thread.
            var upsertScope = VaultDepositPhaseProfile.Begin();

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

            VaultDepositPhaseProfile.End(VaultDepositPhase.Upsert, upsertScope);

            // NO world.SaveBiota(vault) here any more. It is deferred to the end of the drain
            // (dirtyVaults -> FlushDirtyVaultSaves), because these are same-id saves that
            // SerializedShardDatabase cannot batch, so one per item queued 512 of them ahead of other
            // players' shard work on a full sale. The ITEM's save above is untouched and still happens
            // first, and it is the save that records the item's new home.

            var auditScope = VaultDepositPhaseProfile.Begin();

            WriteLog(AccountVaultAction.Deposit, actor, item.WeenieClassId, item.Guid.Full, item.Name, item.StackSize ?? 1);

            VaultDepositPhaseProfile.End(VaultDepositPhase.Audit, auditScope);

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
            //
            // A CLASS row is divisible the same way a group is, and for the same reason: its Count is a
            // number of separate indivisible items, so "2 of 5" means two whole items. It is therefore
            // excluded from this refusal by testing the kind exactly, rather than by "not a ledger
            // row" - which would have made every class withdraw of anything but the whole row refuse.
            if (entry.Kind == VaultEntryKind.StoredItem && !entry.IsGroup && amount > 0 && amount != entry.Count)
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
                    // Null guid for BOTH biota-less kinds: the hook's item-guid parameter names a
                    // stored biota and a class row has none, so passing ObjectGuid.Invalid.Full would
                    // delist a listing over guid 0 if one ever existed. The class display id is what
                    // separates a class withdraw from a ledger withdraw of the same wcid - it is null
                    // for every other kind, so a ledger withdraw can never reach a class listing.
                    preWithdraw(AccountId, entry.Kind == VaultEntryKind.StoredItem ? entry.Guid.Full : (uint?)null, entry.Wcid, amount, entry.ClassDisplayId);
                }
                catch (Exception ex)
                {
                    log.Error($"[VAULT] account {AccountId}: the pre-withdraw hook threw and was ignored: {ex.GetFullMessage()}");
                }
            }

            bool handed;

            switch (entry.Kind)
            {
                case VaultEntryKind.Ledger:
                    handed = WithdrawFromLedger(entry, amount, actor, withdrawn, out failReason);
                    break;

                case VaultEntryKind.Class:
                    handed = WithdrawFromClass(entry, amount, actor, withdrawn, out failReason);
                    break;

                default:
                    handed = entry.IsGroup
                        ? WithdrawGroup(entry, amount, actor, takeOrder, withdrawn, out failReason)
                        : WithdrawItem(entry, actor, withdrawn, out failReason);
                    break;
            }

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
                // See TryDepositBatch's asymmetry remark for why this proceeds where the deposit path
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
        /// Takes <paramref name="amount"/> items off a counted CLASS row, rebuilding each one from the
        /// row's stored canonical form.
        ///
        /// THE POOLED VALUE IS CONSERVED EXACTLY. The row carries a count and a TOTAL, not a canonical
        /// per-item value, because Value is deliberately excluded from class identity (the owner's v1
        /// ruling). Each item handed out therefore takes round(remaining total / remaining count) and
        /// both are decremented, so a withdraw of the whole row hands out a value sum exactly equal to
        /// total_Value - the last item takes whatever is left by construction. See
        /// <see cref="PooledShare"/> for why the arithmetic is integer rather than floating point.
        ///
        /// ONE OBJECT PER ITEM. <see cref="Player.CalcPayoutStackSizes"/> is deliberately NOT used: it
        /// splits a unit count by MaxStackSize, and a class row's items are by construction NOT
        /// stackable (TryDescribeClass refuses anything with a StackSize or MaxStackSize above one).
        /// Running it here would pack N indivisible bags into one "stack" of N that the client would
        /// draw as a single item.
        ///
        /// The ORDERING mirrors <see cref="WithdrawFromLedger"/> exactly, and so does its
        /// deposit/withdraw asymmetry: the audit row is written BEFORE the debit, because the debit is
        /// an autocommitted UPDATE while the items it pays for are built in memory and are not
        /// persisted until the caller delivers them - a crash in that window leaves the row permanently
        /// lower with no biota anywhere, and the early audit row is the only thing that makes the loss
        /// reconstructable. On Refused the balancing Return row is written; on Failed and
        /// AppliedCountUnknown the withdraw PROCEEDS and delivers, because refusing there would write a
        /// Return row over a debit that really happened and erase a real loss from the audit trail.
        ///
        /// IT DRAINS SEVERAL MEMBER ROWS, IN CLASS KEY ASCENDING ORDER. A displayed line can stand for
        /// more than one account_vault_class row (see <see cref="VaultEntry.ClassMembers"/>), so the
        /// amount is taken from the front members first and every guarantee above holds PER MEMBER: its
        /// own payload, its own payout schedule off its own count and total, its own audit row, its own
        /// guarded debit. Nothing about this merges two rows - each one's canonical form is what its
        /// share of the items is rebuilt from, which is what keeps a withdraw exact.
        ///
        /// ALL OR NOTHING ACROSS THE WHOLE DRAIN. A member that refuses, or that cannot build its
        /// objects, unwinds every member already debited - because a partial hand-over would leave the
        /// caller holding objects whose pooled shares no longer sum to anything any row knows about,
        /// which is exactly the reasoning the single-row out-of-objects path already applied.
        ///
        /// AN ANOMALOUS MEMBER IS NEVER SWALLOWED BY THE AGGREGATE. If any member's adjust came back
        /// Failed or AppliedCountUnknown the store's class map is known stale for at least that row, so
        /// <see cref="InvalidateClasses"/> runs for the whole drain rather than the per-member counts
        /// being applied as though everything were known. That is the same outcome the single-row path
        /// produces, and it is what stops a multi-member drain reporting a clean success over a row
        /// nobody can account for.
        /// </summary>
        private bool WithdrawFromClass(VaultEntry entry, int amount, VaultActor actor, List<WorldObject> withdrawn, out string failReason)
        {
            failReason = null;

            if (amount < 1)
            {
                failReason = "Choose how many to withdraw.";
                return false;
            }

            // Refused, not clamped, matching the ledger's and the group's own over-withdraw behaviour.
            if (amount > entry.Count)
            {
                failReason = $"Your vault holds only {entry.Count} of those.";
                return false;
            }

            // PLAN THE WHOLE DRAIN BEFORE ANYTHING IS DEBITED. Every member's payload is parsed and
            // every member's payout schedule is computed up here, so a payload that will not read
            // refuses with no row having moved - which is the single-row path's own guarantee, kept
            // whole rather than degraded into "refuses once some rows have already moved".
            if (!TryPlanClassDrain(entry, amount, out var plan, out failReason))
                return false;

            var created = new List<WorldObject>();
            var committed = new List<ClassDrainStep>();

            var anomalous = false;
            var reap = false;

            foreach (var step in plan)
            {
                // Built BEFORE this member's debit, exactly as WithdrawFromLedger's probe is, and for
                // both of its reasons: the audit row needs a name, and a wcid that can no longer be
                // instantiated must refuse before anything moves. Its Value is this member's first
                // pooled share, so it is a real deliverable rather than a throwaway.
                var probe = world.MaterializeClass(step.Wcid, step.Overrides, step.Shares[0]);

                if (probe == null)
                {
                    log.Error($"[VAULT] account {AccountId}: could not materialize wcid {step.Wcid} for a class withdraw of {step.Take} (class {step.ClassKey}); refusing before that row moves.");

                    UnwindClassDrain(created, committed, actor);

                    failReason = UnavailableMessage;
                    return false;
                }

                step.ProbeName = probe.Name;

                // Audit BEFORE the debit. See this method's remarks and WithdrawFromLedger's for why.
                WriteLog(AccountVaultAction.Withdraw, actor, step.Wcid, null, probe.Name, step.Take, step.ValueLeaving, step.ClassKey);

                var debit = backend.TryAdjustAccountVaultClass(AccountId, step.ClassKey, -step.Take, -step.ValueLeaving, null, out var newCount, out var newTotalValue);

                if (debit == AccountVaultStackAdjustResult.Refused)
                {
                    // The Withdraw row above recorded an ask the database then refused, so it has to be
                    // balanced or the audit trail shows items leaving a vault they never left. Written
                    // ONLY for a provable refusal: writing it for a database failure would cancel out a
                    // debit that may have landed and record a real loss as a non-event.
                    WriteLog(AccountVaultAction.Return, actor, step.Wcid, null, probe.Name, step.Take, step.ValueLeaving, step.ClassKey);

                    world.DestroyItem(probe);

                    UnwindClassDrain(created, committed, actor);

                    failReason = GoneMessage;
                    return false;
                }

                step.Debit = debit;
                committed.Add(step);

                if (debit == AccountVaultStackAdjustResult.Failed)
                {
                    // See DepositToClass's asymmetry remark for why this proceeds where the deposit
                    // path refuses. The items may already be gone from the row and the player has
                    // nothing, so the recoverable direction is to hand them over and leave a marker,
                    // not to write a Return row that erases the debit.
                    log.Error($"[VAULT] CLASS STATE UNKNOWN: account {AccountId}, class {step.ClassKey}, wcid {step.Wcid}, delta -{step.Take} items / -{step.ValueLeaving} value. A withdraw debit neither committed nor provably failed. The items ARE being handed over and NO balancing Return row is written, because writing one over a debit that did land would erase a real loss. If the debit did NOT land, this account has been paid twice for those items - reconcile against account_vault_log.");
                }

                if (debit == AccountVaultStackAdjustResult.Applied)
                {
                    lock (stateLock)
                        ApplyClassCountLocked(step.ClassKey, step.Wcid, step.CanonicalForm, step.Overrides, newCount, newTotalValue);

                    // Gated on Applied as well as on the count, exactly as the ledger path is: newCount
                    // is 0 for every other outcome, and 0 there is "unknown", not "the row is empty".
                    if (newCount <= 0)
                        reap = true;
                }
                else
                {
                    anomalous = true;
                }

                var built = new List<WorldObject> { probe };

                var shortfall = false;

                for (var i = 1; i < step.Take; i++)
                {
                    var more = world.MaterializeClass(step.Wcid, step.Overrides, step.Shares[i]);

                    if (more == null)
                    {
                        // ALL OR NOTHING, matching WithdrawFromLedger's own out-of-objects path:
                        // everything already built is destroyed and the WHOLE debit is refunded, rather
                        // than the undelivered tail alone.
                        log.Error($"[VAULT] account {AccountId}: ran out of objects part way through a class withdraw of {step.Take} from {step.ClassKey}; destroying the {built.Count} already built for that row and refunding its whole debit.");

                        foreach (var orphan in built)
                            world.DestroyItem(orphan);

                        // THE SECOND REFUND SITE. step.Debit was assigned above, so this can be
                        // refunding a Failed or AppliedCountUnknown member with exactly the risk shape
                        // the unwind's own refunds carry - and it reaches RefundClass without going
                        // through UnwindClassDrain, because it removes itself from `committed` below.
                        // Both sites call the same helper so a third one is a one-line call rather
                        // than a copied block.
                        LogRefundOverUnknownDebit(step);

                        RefundClass(step.ClassKey, step.Wcid, step.CanonicalForm, step.Overrides, step.Take, step.ValueLeaving);
                        WriteLog(AccountVaultAction.Return, actor, step.Wcid, null, probe.Name, step.Take, step.ValueLeaving, step.ClassKey);

                        // Already refunded by hand, so it must not be refunded a second time by the
                        // unwind below.
                        committed.Remove(step);

                        shortfall = true;
                        break;
                    }

                    built.Add(more);
                }

                if (shortfall)
                {
                    UnwindClassDrain(created, committed, actor);

                    failReason = UnavailableMessage;
                    return false;
                }

                created.AddRange(built);
            }

            withdrawn.AddRange(created);

            if (anomalous)
                InvalidateClasses($"a withdraw of {amount} item(s) across {plan.Count} row(s) of class {entry.ClassKey} where at least one debit left an unknown resulting count");

            if (reap)
            {
                // On the mutation queue, which is the same placement and the same load-bearing reason
                // WithdrawFromLedger's reap has: TryAdjustAccountVaultClass is an ensure-row statement
                // followed by a guarded UPDATE, and a concurrent delete landing between the two would
                // make a legitimate deposit return false.
                backend.DeleteEmptyAccountVaultClasses(AccountId);
            }

            return true;
        }

        /// <summary>
        /// One member row's share of a multi-row class withdraw: which row, how many items off it, and
        /// the exact payout schedule those items take.
        /// </summary>
        private sealed class ClassDrainStep
        {
            public string ClassKey;
            public string CanonicalForm;
            public uint Wcid;
            public VaultItemClassOverrides Overrides;
            public int Take;
            public List<int> Shares;
            public long ValueLeaving;

            /// <summary>The name the audit rows for this member carry, captured off its probe.</summary>
            public string ProbeName;

            /// <summary>
            /// What this member's debit actually returned, recorded so <see cref="UnwindClassDrain"/>
            /// can tell a refund it can prove from one it is taking a deliberate risk on. Left at
            /// <see cref="AccountVaultStackAdjustResult.Refused"/> until the debit runs, which is the
            /// one outcome that is never committed and therefore never unwound.
            /// </summary>
            public AccountVaultStackAdjustResult Debit = AccountVaultStackAdjustResult.Refused;
        }

        /// <summary>
        /// Works out which member rows a withdraw of <paramref name="amount"/> comes off and what each
        /// one pays out, WITHOUT touching anything.
        ///
        /// Every payload is parsed here rather than inside the commit loop, so a row this build cannot
        /// read refuses the whole withdraw with nothing debited - the "refuse before the row moves"
        /// guarantee the single-row path has always had, extended over the group rather than weakened
        /// into a partial.
        ///
        /// Each member's schedule is computed off ITS OWN count and total, never off the line's
        /// summed ones. That is what makes the pool conserved per row: a full drain of a member hands
        /// out exactly its total_Value, because the last item takes whatever is left.
        /// </summary>
        private bool TryPlanClassDrain(VaultEntry entry, int amount, out List<ClassDrainStep> plan, out string failReason)
        {
            plan = new List<ClassDrainStep>();
            failReason = null;

            var remaining = amount;

            foreach (var member in entry.ClassMembers)
            {
                if (remaining <= 0)
                    break;

                if (member.Count <= 0)
                    continue;

                var take = (int)Math.Min(remaining, member.Count);

                // The payload is parsed from the MEMBER's own canonical form rather than re-read from
                // the shard, so the items handed over are built from exactly the text the row this
                // withdraw was resolved against carries. It is also re-checked against the line's wcid,
                // because the denormalized column and the payload must agree before anything is built
                // from either.
                if (!VaultItemClass.TryParseCanonicalForm(member.CanonicalForm, out var wcid, out var overrides, out _)
                    || wcid != entry.Wcid)
                {
                    log.Error($"[VAULT] account {AccountId}: could not rebuild the payload of class {member.ClassKey} (wcid {entry.Wcid}) for a withdraw of {amount}; refusing before any row moves. Nothing was debited and nothing was destroyed.");

                    plan = null;
                    failReason = UnavailableMessage;
                    return false;
                }

                // The payout schedule. The pool is decremented by the sum handed out rather than by a
                // share recomputed afterwards, which is what makes the two agree by construction; and
                // it is computed before the audit row because that row has to carry the value that
                // leaves.
                var shares = new List<int>(take);

                var remainingTotal = member.TotalValue;
                var remainingCount = member.Count;

                for (var i = 0; i < take; i++)
                {
                    var share = PooledShare(remainingTotal, remainingCount);

                    shares.Add(share);

                    remainingTotal -= share;
                    remainingCount--;
                }

                long valueLeaving = 0;

                foreach (var share in shares)
                    valueLeaving += share;

                plan.Add(new ClassDrainStep
                {
                    ClassKey = member.ClassKey,
                    CanonicalForm = member.CanonicalForm,
                    Wcid = wcid,
                    Overrides = overrides,
                    Take = take,
                    Shares = shares,
                    ValueLeaving = valueLeaving,
                });

                remaining -= take;
            }

            if (remaining > 0)
            {
                // Unreachable through the amount check above, which tests against the line's summed
                // count - this fires only if that sum and the members disagree, which would mean the
                // entry was built from something other than its own members.
                log.Error($"[VAULT] account {AccountId}: a class withdraw of {amount} from {entry.ClassKey} could only be planned across {amount - remaining} item(s) of its member rows; refusing before anything moves.");

                plan = null;
                failReason = GoneMessage;
                return false;
            }

            return true;
        }

        /// <summary>
        /// Puts a part-finished multi-row class withdraw back: destroys every object built so far and
        /// refunds every member whose debit already moved.
        ///
        /// REFUNDS EVERY COMMITTED MEMBER, whatever its outcome was, which is exactly what the
        /// single-row out-of-objects path has always done with its own debit. A Failed debit that in
        /// truth never landed is the one case this over-refunds, and <see cref="RefundClass"/> is
        /// already the guarded way that risk is taken: the alternative is destroying items the player
        /// paid for and leaving no row naming them. That is the standing ruling for the whole withdraw
        /// path - never destroy property on an unknown outcome - and it is not revisited here.
        ///
        /// WHAT THIS DIFF DID CHANGE IS THE BLAST RADIUS, from one row per call to N, so every refund
        /// over a non-Applied debit gets its own log marker through
        /// <see cref="LogRefundOverUnknownDebit"/>. It is deliberately NOT folded into the line above:
        /// an operator needs to find exactly these events, and they are rare enough to be worth their
        /// own grep. This is NOT the only refund site - the mid-build shortfall inside
        /// <see cref="WithdrawFromClass"/> refunds its own member and removes it from
        /// <paramref name="committed"/> before this ever runs - which is why the marker lives in a
        /// helper both of them call. See Docs/MuleVendor/CLASS-TIER-ENTRY-COUNT-FIX.md, Change 5, for
        /// the recorded risk.
        /// </summary>
        private void UnwindClassDrain(List<WorldObject> created, List<ClassDrainStep> committed, VaultActor actor)
        {
            if (created.Count == 0 && committed.Count == 0)
                return;

            log.Error($"[VAULT] account {AccountId}: unwinding a class withdraw part way through - destroying {created.Count} object(s) already built and refunding {committed.Count} member row(s).");

            foreach (var orphan in created)
                world.DestroyItem(orphan);

            created.Clear();

            // Last debited, first refunded, so the audit trail reads as an unwind rather than as a
            // second pass in the same direction.
            for (var i = committed.Count - 1; i >= 0; i--)
            {
                var step = committed[i];

                LogRefundOverUnknownDebit(step);

                RefundClass(step.ClassKey, step.Wcid, step.CanonicalForm, step.Overrides, step.Take, step.ValueLeaving);
                WriteLog(AccountVaultAction.Return, actor, step.Wcid, null, step.ProbeName, step.Take, step.ValueLeaving, step.ClassKey);
            }

            committed.Clear();
        }

        /// <summary>
        /// The <c>[VAULT] CLASS REFUND OVER UNKNOWN DEBIT</c> marker, emitted when a class withdraw
        /// refunds a member whose debit did not provably apply. A no-op for an
        /// <see cref="AccountVaultStackAdjustResult.Applied"/> debit, which is an ordinary refund and
        /// must not be marked - burying the rare event under the common one is the same as not having
        /// the marker at all.
        ///
        /// ONE DEFINITION, AND THE GUARD LIVES IN HERE RATHER THAN AT THE CALL SITES. There are two
        /// refund paths in <see cref="WithdrawFromClass"/> and they are easy to miss: the unwind, and
        /// the member that runs out of objects part way through its own build and refunds itself
        /// directly. A review found the second one uncovered after the first had been marked for a
        /// whole commit. Every call site is now a bare call, so a third refund path added later gets
        /// this for one line and cannot forget the condition.
        ///
        /// CALL IT BEFORE THE REFUND, never after, so a marker exists even if
        /// <see cref="RefundClass"/> throws or the process dies between the two.
        ///
        /// The risk clause differs by outcome because the two are not equally dangerous.
        /// <see cref="AccountVaultStackAdjustResult.Failed"/> means the adjust neither committed nor
        /// provably failed, so the refund may credit items that were never taken;
        /// <see cref="AccountVaultStackAdjustResult.AppliedCountUnknown"/> means the delta really did
        /// commit and only the read-back was lost, so the refund is arithmetically right and only the
        /// row's resulting count is in doubt. Saying "unknown" for both would send an operator hunting
        /// a phantom every time the second one fires.
        /// </summary>
        private void LogRefundOverUnknownDebit(ClassDrainStep step)
        {
            if (step.Debit == AccountVaultStackAdjustResult.Applied)
                return;

            var risk = step.Debit == AccountVaultStackAdjustResult.Failed
                ? "that debit neither committed nor provably failed, so this refund MAY HAVE CREATED items and value that were never taken"
                : "that debit committed but its resulting count could not be read back, so the refund is arithmetically right while the row's count is unconfirmed";

            log.Error($"[VAULT] CLASS REFUND OVER UNKNOWN DEBIT: account {AccountId}, class {step.ClassKey}, wcid {step.Wcid}, refunding +{step.Take} item(s) / +{step.ValueLeaving} value over a debit that returned {step.Debit} - {risk}. Refunding anyway is the deliberate ruling (Docs/MuleVendor/CLASS-TIER-ENTRY-COUNT-FIX.md, Change 5): the alternative destroys items a player paid for whenever the debit really did land. TO RECONCILE: the Withdraw and Return rows for class {step.ClassKey} on account {AccountId} in account_vault_log bracket this event, and account_vault_class.Count for that key is the number they have to agree with.");
        }

        /// <summary>
        /// Puts items and their pooled value back on a class row after a withdraw could not build them
        /// all. <see cref="RefundLedger"/>'s counterpart, and it shares that method's contract: an
        /// unknown outcome is NOT a failed refund, so the row is re-read rather than reported lost.
        ///
        /// The seed is supplied because the row may have been reaped to zero and deleted between the
        /// debit and this call, which is exactly the case where a refund that could not create a row
        /// would lose the items with nothing left naming them.
        /// </summary>
        private void RefundClass(string classKey, uint wcid, string canonicalForm, VaultItemClassOverrides overrides, long items, long value)
        {
            if (items <= 0 && value <= 0)
                return;

            var seed = new AccountVaultClassSeed
            {
                Wcid = wcid,
                ValueBandPct = VaultItemClass.ValueExcluded,
                CanonicalForm = canonicalForm,
            };

            var refund = backend.TryAdjustAccountVaultClass(AccountId, classKey, items, value, seed, out var restoredCount, out var restoredValue);

            if (refund == AccountVaultStackAdjustResult.Applied)
            {
                lock (stateLock)
                    ApplyClassCountLocked(classKey, wcid, canonicalForm, overrides, restoredCount, restoredValue);

                return;
            }

            InvalidateClasses($"a refund of {items} item(s) to class {classKey} whose resulting count could not be read back");
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
                        ForgetFoldMemoryLocked(item.Guid.Full);

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
                ForgetFoldMemoryLocked(entry.Guid.Full);
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

                // On the way IN as well as on the way out. An item that left and came back may have
                // been changed while it was out, so anything the fold remembers about it is an answer
                // about a different item. Safe against the fold's own AbortPass path, which calls this
                // method and only THEN records its refusal.
                ForgetFoldMemoryLocked(item.Guid.Full);
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

        /// <summary>
        /// The undo of a CLASS withdrawal: puts an object <see cref="WithdrawFromClass"/> built back into
        /// the class it came out of, crediting one item and exactly its own Value to that class row,
        /// and destroys the carrier.
        ///
        /// It exists because neither sibling undo is right for a class-sourced object.
        /// <see cref="TryReturnWithdrawnToLedger"/> would credit the collapsed STACK ledger - a
        /// different holding, and plain template items in place of the ones withdrawn - and
        /// <see cref="TryReturnWithdrawn"/> would file it as a stored biota, which moves value out of
        /// the pooled total and bypasses the entry cap on a thing that was never counted as a biota.
        /// The object's Value IS the pooled share it was handed (VaultItemClass materializes it that
        /// way), so crediting it back restores the row's count and total exactly.
        ///
        /// Like its siblings it is NOT a deposit: no access check (this is the undo of a withdraw the
        /// actor was already authorized for) and NO ENTRY CAP, because the items were counted while
        /// they sat on the row moments ago. The class is re-derived from the object itself, OUTSIDE
        /// every lock (TryDescribeClass takes the item's own BiotaDatabaseLock, which is NoRecursion),
        /// and deliberately IGNORES account_vault_class_storage: that switch stops NEW items
        /// collapsing, and putting an item back into the class it left microseconds ago is not new.
        ///
        /// If the object does not describe as a class at all, it falls back to
        /// <see cref="TryReturnWithdrawn"/> and keeps its biota - the direction that never destroys an
        /// item. It NEVER goes to the stack ledger.
        ///
        /// A credit whose outcome is UNKNOWN answers false and keeps the object: retrying the return
        /// would double a credit that did land, and filing the object as a biota would duplicate it.
        /// The caller logs LOST ITEM RISK with the guid, which is the operator's reconciliation handle.
        /// </summary>
        internal bool TryReturnWithdrawnToClass(WorldObject item, VaultActor actor)
        {
            AssertOnMutationQueue(nameof(TryReturnWithdrawnToClass));

            Touch();

            if (item == null)
                return false;

            lock (stateLock)
            {
                if (!CheckReadyLocked(out _))
                {
                    log.Error($"[VAULT] account {AccountId}: could not return 0x{item.Guid.Full:X8} to its class because the store is not ready. The CALLER still holds it and must not destroy it.");
                    return false;
                }
            }

            if (item.ContainerId != null || item.WielderId != null)
            {
                log.Error($"[VAULT] account {AccountId}: refused a class return of 0x{item.Guid.Full:X8} because it is still parented (container {item.ContainerId}, wielder {item.WielderId}). A delivered item is not a returnable one.");
                return false;
            }

            // Outside every lock - see this method's remarks.
            if (!world.TryDescribeClass(item, out var overrides))
            {
                log.Warn($"[VAULT] account {AccountId}: 0x{item.Guid.Full:X8} (wcid {item.WeenieClassId}) came out of a class row but no longer describes as a class; returning it as a stored biota instead, which keeps it whole.");
                return TryReturnWithdrawn(item, actor);
            }

            var classKey = VaultItemClass.ClassKey(item.WeenieClassId, overrides, VaultItemClass.ValueExcluded);

            depositCommit = VaultDepositCommit.None;

            if (DepositToClass(item, actor, classKey, overrides, AccountVaultAction.Return, out var failReason))
                return true;

            // Committed and then threw inside DepositToClass is not reachable here (a throw escapes),
            // so what is left is Refused (None) or unknown (Unknown).
            if (depositCommit == VaultDepositCommit.None)
            {
                log.Warn($"[VAULT] account {AccountId}: the class row refused the return of 0x{item.Guid.Full:X8} into class {classKey} ({failReason}); returning it as a stored biota instead, which keeps it whole.");
                return TryReturnWithdrawn(item, actor);
            }

            log.Error($"[VAULT] account {AccountId}: the return of 0x{item.Guid.Full:X8} into class {classKey} has an UNKNOWN outcome. It is NOT retried and NOT filed as a biota, either of which would duplicate it if the credit landed. The caller still holds it.");
            return false;
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
            if (entry.Kind == VaultEntryKind.StoredItem && !entry.IsGroup && amount != entry.Count)
            {
                failReason = "A stored item can only be destroyed whole.";
                return false;
            }

            if (IsListed(entry))
            {
                failReason = ItemListedMessage;
                return false;
            }

            // A counted CLASS line used to be refused here, because account_vault_barrel recorded only
            // (wcid, count) and a restore of it would have credited the collapsed STACK ledger - a
            // different holding. The row now carries the member's class key, canonical form and value
            // (2026-09-27-00-Market-Listing-And-Barrel-Class-Key.sql), and TryRestoreFromBarrel routes a
            // row carrying a class key to RestoreClassFromBarrel BEFORE its null-guid ledger branch, so
            // the refusal's reason is gone.
            switch (entry.Kind)
            {
                case VaultEntryKind.Ledger:
                    return BarrelLedger(entry, amount, actor, out failReason);

                case VaultEntryKind.Class:
                    return BarrelClass(entry, amount, actor, out failReason);

                default:
                    return BarrelStoredItems(entry, amount, actor, out failReason);
            }
        }

        /// <summary>
        /// Barrels <paramref name="amount"/> items off a counted CLASS line. Like a ledger barreling
        /// there is no biota, so nothing moves anywhere: each member row the amount comes off is
        /// debited, and one account_vault_barrel row per member records what left it.
        ///
        /// THE SHARES ARE <see cref="TryPlanClassDrain"/>'S, the same plan a withdrawal of the same
        /// amount would pay out, so barreling k items removes exactly the value withdrawing those k
        /// would have handed over, and a restore puts exactly that back.
        ///
        /// ALL OR NOTHING ON THE DEBITS, and the debits come FIRST, as in <see cref="BarrelLedger"/>:
        /// a barrel row written over a debit that never landed is a dupe waiting for the first restore.
        /// A member whose debit is REFUSED - the one outcome that proves it did not move - refunds every
        /// member already debited and the barreling is refused with nothing recorded. A Failed or
        /// count-unknown debit is treated as the ledger path treats one: the barreling proceeds and
        /// its row IS written, because if the debit did land the row is the only thing that makes the
        /// items restorable, and the store re-reads its class map rather than guess a count.
        ///
        /// No audit row is written until every debit has run, so a refused barreling leaves no
        /// Barrel row to balance - the refunds below it put the members back and nothing claims
        /// otherwise.
        /// </summary>
        private bool BarrelClass(VaultEntry entry, int amount, VaultActor actor, out string failReason)
        {
            failReason = null;

            if (!TryPlanClassDrain(entry, amount, out var plan, out failReason))
                return false;

            var committed = new List<ClassDrainStep>();
            var anomalous = false;
            var reap = false;

            foreach (var step in plan)
            {
                var debit = backend.TryAdjustAccountVaultClass(AccountId, step.ClassKey, -step.Take, -step.ValueLeaving, null, out var newCount, out var newTotalValue);

                if (debit == AccountVaultStackAdjustResult.Refused)
                {
                    log.Error($"[VAULT] account {AccountId}: the class ledger refused a barreling of {step.Take} item(s) off class {step.ClassKey} (wcid {step.Wcid}); refunding the {committed.Count} member(s) already debited. Nothing is recorded as barreled.");

                    for (var i = committed.Count - 1; i >= 0; i--)
                    {
                        var undo = committed[i];

                        LogRefundOverUnknownDebit(undo);
                        RefundClass(undo.ClassKey, undo.Wcid, undo.CanonicalForm, undo.Overrides, undo.Take, undo.ValueLeaving);
                    }

                    if (committed.Count > 0)
                        Interlocked.Increment(ref version);

                    failReason = UnavailableMessage;
                    return false;
                }

                step.Debit = debit;
                committed.Add(step);

                if (debit == AccountVaultStackAdjustResult.Applied)
                {
                    lock (stateLock)
                        ApplyClassCountLocked(step.ClassKey, step.Wcid, step.CanonicalForm, step.Overrides, newCount, newTotalValue);

                    if (newCount <= 0)
                        reap = true;
                }
                else
                {
                    anomalous = true;

                    if (debit == AccountVaultStackAdjustResult.Failed)
                        log.Error($"[VAULT] CLASS STATE UNKNOWN: account {AccountId}, class {step.ClassKey}, wcid {step.Wcid}, delta -{step.Take} items / -{step.ValueLeaving} value. A barrel debit neither committed nor provably failed. The account_vault_barrel row IS being written, because it is the only thing that makes the items restorable if the debit did land - reconcile it against account_vault_log before restoring.");
                }
            }

            if (anomalous)
                InvalidateClasses($"a barreling of {amount} item(s) across {plan.Count} row(s) of class line {entry.ClassDisplayId} where at least one debit left an unknown resulting count");

            // Bumped the moment the items are committed to being gone, not on the success return.
            Interlocked.Increment(ref version);

            foreach (var step in plan)
            {
                var name = step.Overrides?.GetString(PropertyString.Name);

                if (string.IsNullOrEmpty(name))
                {
                    // A probe purely for the name, as BarrelLedger builds one; a wcid dropped from
                    // ace_world must not refuse here, because the debit has already happened.
                    var probe = world.CreateNewWorldObject(step.Wcid);
                    name = probe?.Name ?? $"Item {step.Wcid}";

                    if (probe != null)
                        world.DestroyItem(probe);
                }

                step.ProbeName = name;

                var row = new AccountVaultBarrel
                {
                    AccountId = AccountId,
                    Wcid = step.Wcid,
                    ItemGuid = null,
                    Count = step.Take,
                    ItemName = name,
                    BarreledAt = DateTime.UtcNow,
                    ActorCharacterGuid = actor.CharacterGuid,
                    ActorCharacterName = string.IsNullOrEmpty(actor.Name) ? "<unknown>" : actor.Name,
                    ClassKey = step.ClassKey,
                    CanonicalForm = step.CanonicalForm,
                    Value = step.ValueLeaving,
                };

                if (!backend.AddAccountVaultBarrel(row))
                {
                    // Same shape and same answer as BarrelLedger's missing row: the items are gone and
                    // cannot be put back without risking a dupe over a debit whose state may itself be
                    // unknown, so account_vault_log below is the only record.
                    log.Error($"[VAULT] account {AccountId}: BARREL ROW MISSING for {step.Take} item(s) of class {step.ClassKey} (wcid {step.Wcid}, {name}, value {step.ValueLeaving}). The items are debited and are NOT restorable through /vaultrestore; account_vault_log is the only record.");
                }
            }

            if (reap)
                backend.DeleteEmptyAccountVaultClasses(AccountId);

            foreach (var step in plan)
                WriteLog(AccountVaultAction.Barrel, actor, step.Wcid, null, step.ProbeName, step.Take, step.ValueLeaving, step.ClassKey);

            return true;
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
                // Null guid for BOTH biota-less kinds, same as the pre-withdraw hook: the parameter
                // names a stored biota and a class row has none. The class display id is null for
                // every kind but a class row, so a ledger row never answers for a class listing.
                return hook(AccountId, entry.Kind == VaultEntryKind.StoredItem ? entry.Guid.Full : (uint?)null, entry.Wcid, entry.ClassDisplayId);
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
                        ForgetFoldMemoryLocked(item.Guid.Full);

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

            // A CLASS barreling first, and before the null-guid test below: a class row carries a null
            // item guid exactly as a ledger row does, and falling into the ledger branch would credit
            // the collapsed STACK ledger for its wcid - a different holding, and the very reason class
            // barrelings were once refused outright.
            if (row.ClassKey != null)
                return RestoreClassFromBarrel(row, actor, out failReason);

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

        /// <summary>
        /// Puts one CLASS barreling back: re-credits its member row with the items and the exact value
        /// that left it, re-creating the member from the row's canonical form if it was reaped to zero
        /// in the meantime. Never touches the stack ledger.
        ///
        /// Same discipline as the other two branches of <see cref="TryRestoreFromBarrel"/>: everything
        /// that can refuse (readiness, a payload this build cannot read, the entry cap) happens before
        /// anything moves, and RestoredAt is stamped only AFTER the credit. An unknown credit outcome
        /// is resolved the way <see cref="RestoreLedgerFromBarrel"/> resolves one - toward stamping the
        /// row, because a second restore over a credit that did land pays the account twice.
        ///
        /// THE PAYLOAD IS CHECKED AGAINST THE KEY before it is used. The class key is a hash of exactly
        /// the canonical form, so recomputing it from the parsed payload proves the row's two columns
        /// still describe one class; a mismatch refuses and leaves the row open for an operator rather
        /// than seeding a class row whose key and contents disagree.
        /// </summary>
        private bool RestoreClassFromBarrel(AccountVaultBarrel row, VaultActor actor, out string failReason)
        {
            failReason = null;

            if (string.IsNullOrEmpty(row.CanonicalForm)
                || !VaultItemClass.TryParseCanonicalForm(row.CanonicalForm, out var wcid, out var overrides, out _)
                || wcid != row.Wcid
                || !string.Equals(VaultItemClass.ClassKey(wcid, overrides, VaultItemClass.ValueExcluded), row.ClassKey, StringComparison.Ordinal))
            {
                log.Error($"[VAULT] account {AccountId}: account_vault_barrel row {row.Id} carries class {row.ClassKey} (wcid {row.Wcid}) but its canonical form is missing, unreadable, or does not hash to that key. Nothing was moved and the row is left open.");
                failReason = $"Barrel row {row.Id} carries a class payload this server cannot read back.";
                return false;
            }

            var displayKey = VaultCollapse.ClassDisplayGroupKey(wcid, overrides);

            lock (stateLock)
            {
                if (!CheckReadyLocked(out failReason))
                    return false;

                // The cap counts DISPLAY GROUPS for class rows (AddsEntryLocked): a member re-joining a
                // line the account still draws costs nothing, and only a line that is gone costs one.
                var newEntries = ClassDisplayGroupIndexLocked().ContainsKey(displayKey) ? 0 : 1;

                if (!HasRoomForLocked(newEntries))
                {
                    failReason = NoRoomToRestoreMessage;
                    return false;
                }
            }

            var value = row.Value ?? 0;

            if (value < 0)
                value = 0;

            // The seed is what re-creates a member that was reaped to zero after the barreling; it is
            // ignored by the DAO when the row still exists.
            var seed = new AccountVaultClassSeed
            {
                Wcid = wcid,
                ValueBandPct = VaultItemClass.ValueExcluded,
                CanonicalForm = row.CanonicalForm,
            };

            var credit = backend.TryAdjustAccountVaultClass(AccountId, row.ClassKey, row.Count, value, seed, out var newCount, out var newTotalValue);

            if (credit == AccountVaultStackAdjustResult.Refused)
            {
                log.Error($"[VAULT] account {AccountId}: the class ledger refused a restore of {row.Count} item(s) / {value} value into class {row.ClassKey} (barrel row {row.Id}). The row is left open.");
                failReason = UnavailableMessage;
                return false;
            }

            if (credit == AccountVaultStackAdjustResult.Applied)
            {
                lock (stateLock)
                    ApplyClassCountLocked(row.ClassKey, wcid, row.CanonicalForm, overrides, newCount, newTotalValue);
            }
            else
            {
                log.Error($"[VAULT] CLASS STATE UNKNOWN: account {AccountId}, class {row.ClassKey}, wcid {row.Wcid}, delta +{row.Count} items / +{value} value (barrel row {row.Id}). A restore credit could not be confirmed. The row IS being stamped restored, because a second restore against it would be a dupe; if the credit did not land, re-credit it by hand from account_vault_log.");

                InvalidateClasses($"a barrel restore of {row.Count} item(s) into class {row.ClassKey} with an unconfirmed outcome");
            }

            Interlocked.Increment(ref version);

            StampRestored(row);

            WriteLog(AccountVaultAction.Restore, actor, row.Wcid, null, row.ItemName, row.Count, value, row.ClassKey);

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
                // purge is the stamp and nothing else. A CLASS barreling is the same shape (its row
                // carries a class key and a null guid), and must stay stamp-only: it debited its member
                // row at barrel time, and there is no object anywhere to destroy.
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

                // Which of the owner's characters issued this grant, so /mule search all can label
                // the shared mule after the character the grantee actually dealt with. The same
                // actor WriteLog records below, so the audit row and the grant row agree.
                GrantedByCharacterGuid = owner.CharacterGuid,
                GrantedByCharacterName = string.IsNullOrEmpty(owner.Name) ? null : owner.Name,
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
            WriteLog(action, actor, wcid, itemGuid, itemName, count, null, null);
        }

        /// <summary>
        /// The class tier's overload. <paramref name="value"/> is the Value that entered the pool on
        /// a deposit or fold, or the summed pooled share that left on a withdraw;
        /// <paramref name="classKey"/> says which pool.
        ///
        /// Both are NULL on every other action, and null is not the same as zero here: 0 is a real
        /// value a worthless item can carry, while null means this action does not move pooled value
        /// at all. An investigation reading the log has to be able to tell those apart.
        /// </summary>
        private void WriteLog(AccountVaultAction action, VaultActor actor, uint wcid, uint? itemGuid, string itemName, long count,
                              long? value, string classKey)
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
                Value = value,
                ClassKey = classKey,
                Timestamp = DateTime.UtcNow,
            };

            // Handed to the background writer rather than written inline. The inline call opened a
            // ShardDbContext and ran SaveChanges - one blocking MySQL round trip per audited operation,
            // which on a mule sale meant one PER DEPOSITED ITEM on the world thread. Nothing a deposit
            // needs in order to be correct depends on the row having landed: the ledger credit, the
            // destroy and the biota saves have all already happened by the time this runs.
            //
            // A failure is still logged and swallowed - by the DAO, and now also by the writer, which
            // additionally catches a THROW so one cannot escape onto a player's thread. Refusing a
            // deposit because the audit write failed would turn a logging outage into a storage outage.
            //
            // With the writer unstarted (every unit test, and any process that never calls
            // AccountVaultAuditWriter.Initialize) this is still exactly the old synchronous single-row
            // write, in order, visible the moment it returns.
            AccountVaultAuditWriter.Write(backend, row);
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
