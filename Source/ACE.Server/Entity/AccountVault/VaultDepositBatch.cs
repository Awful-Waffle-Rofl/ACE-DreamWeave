using System;
using System.Collections.Generic;

using ACE.Database;
using ACE.Server.WorldObjects;

namespace ACE.Server.Entity.AccountVault
{
    /// <summary>
    /// The entry-cap claims a deposit has already made but which the store cannot see yet, because the
    /// lines they create are DEFERRED to the batch's phase B and only become visible when phase D's
    /// re-read lands.
    ///
    /// A claim is recorded per LINE - the wcid for a pristine item, the DISPLAY group key for a class
    /// item - so 53 items of one new wcid consume ONE entry between them rather than 53. This is
    /// <see cref="AccountVaultStore.TryDepositBatch"/>'s own batch-local set, extracted to a type only so
    /// a coalescing deposit window can carry the identical state across several calls; the rule itself is
    /// unchanged and lives where it always did.
    ///
    /// NOTHING HERE IS A COUNT. It carries which LINES exist, never how many units are on them, which is
    /// why carrying it forward cannot violate the batch contract's guarantee 4 (in-memory ledger and class
    /// COUNTS come from a fresh whole-account read, never from arithmetic on the deltas).
    ///
    /// ONE OF THESE PER TABLE, and the split is not tidiness. A window's carried claims are only correct
    /// while the table they describe is still un-re-read: the moment that table reloads, its claimed lines
    /// are in <c>ledger</c> or <c>classes</c> and <see cref="AccountVaultStore"/>'s own entry count includes
    /// them, so a surviving claim is counted TWICE. The two tables reload independently - a foreign reader,
    /// or even the same thread calling EntryCount inside the window, reloads exactly the table it needs -
    /// so a single shared set could not be dropped at the right moment for both. Keeping one per table lets
    /// each set die with its own presence set, in the same statement.
    /// </summary>
    internal sealed class VaultEntryClaims
    {
        /// <summary>Claim keys, "w:&lt;wcid&gt;" for a ledger line and "d:&lt;displayKey&gt;" for a class line.</summary>
        internal readonly HashSet<string> Keys = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// How many entries those claims consume. Counted separately from <see cref="Keys"/> because a
        /// class line whose display key could not be derived still consumes an entry while being
        /// unrecognisable to a later item - it simply cannot be shared.
        /// </summary>
        internal int Entries;

        /// <summary>
        /// Drops every claim, because the table they describe has just been re-read and now answers for
        /// itself. Both halves go together: a surviving key would be harmless (the reloaded table answers
        /// the same question) but a surviving <see cref="Entries"/> is counted on top of the reloaded entry
        /// count, which over-charges the cap for the rest of the window and refuses deposits that fit.
        ///
        /// ONLY LEGITIMATE WHEN EVERY CLAIM'S CREDIT HAS ALREADY COMMITTED, which is what
        /// <see cref="VaultDepositWindow.ClaimsInFlight"/> exists to establish. A claim recorded in phase A
        /// describes a line whose row is created in phase B; dropping it in between removes it from the cap
        /// sum while the re-read that was supposed to replace it could not have seen it either, and the
        /// account is then UNDER-counted and allowed past its cap.
        /// </summary>
        internal void Clear()
        {
            Keys.Clear();
            Entries = 0;
        }
    }

    /// <summary>
    /// A window over several consecutive <see cref="AccountVaultStore.TryDeposit"/> calls on ONE store,
    /// inside ONE serialized region, during which they share a single phase-D re-read instead of paying
    /// one whole-account read each.
    ///
    /// WHY IT EXISTS. The batch path replaced the single-item read-back with one whole-account reload, which
    /// is the intended win when a sale is ONE batch of N items. The market's delivery path is not that
    /// shape: <see cref="ACE.Server.Managers.Market.VaultMarketItemStore.TryGiveToBuyer"/> must keep its
    /// per-item TryDeposit loop, because its per-item commit classification decides whether each item
    /// returns to the seller and a wrong answer there is a dupe or a loss. So a 50-item delivery paid 50
    /// whole-account reads, each of which, on the class table, drags every class row's canonical_Form TEXT.
    ///
    /// WHAT IT DEFERS AND WHAT IT DOES NOT. Only the phase-D re-read moves. Every credit still commits at
    /// its own item's position, every destroy still follows its own credit, and every item's outcome is
    /// still decided exactly where it was. The reload-pending flags are left SET while the window is open,
    /// so any OTHER reader of this store either re-reads successfully or is refused - it can never be
    /// served a count the store knows is stale. What the window changes is that the DEPOSIT path itself
    /// tolerates those flags for the tables it deferred, which is safe because the only in-memory state the
    /// deposit path reads is line PRESENCE (the cap rule) and presence is carried here explicitly.
    /// </summary>
    internal sealed class VaultDepositWindow : IDisposable
    {
        private readonly Action<VaultDepositWindow> close;

        private bool closed;

        internal VaultDepositWindow(Action<VaultDepositWindow> close)
        {
            this.close = close;
        }

        /// <summary>
        /// The stack ledger's cap claims made so far in this window. Paired with
        /// <see cref="LedgerPresent"/>: both describe lines this window created in account_vault_stack that
        /// the in-memory ledger cannot see yet, and both are dropped the moment that table is re-read -
        /// the claims only if <see cref="ClaimsInFlight"/> is clear, which is where the two part company.
        /// See <see cref="VaultEntryClaims"/> for why there is one per table.
        /// </summary>
        internal readonly VaultEntryClaims LedgerClaims = new VaultEntryClaims();

        /// <summary><see cref="LedgerClaims"/>'s counterpart for account_vault_class, paired with <see cref="ClassPresent"/>.</summary>
        internal readonly VaultEntryClaims ClassClaims = new VaultEntryClaims();

        /// <summary>
        /// Stack-ledger wcids this window has PROVED present, by a credit the database reported applied.
        /// Every delta is strictly positive and no stored count is negative, so an applied credit means the
        /// row exists with a count above zero - which is exactly what the DAO's "already present" oracle
        /// means. Carried because the oracle is otherwise read off the stale in-memory ledger, and calling a
        /// present row absent sends it down the INSERT..ON DUPLICATE KEY UPDATE branch, which burns an
        /// AUTO_INCREMENT id per call for a row that already exists.
        /// </summary>
        internal readonly HashSet<uint> LedgerPresent = new HashSet<uint>();

        /// <summary><see cref="LedgerPresent"/>'s counterpart for account_vault_class keys.</summary>
        internal readonly HashSet<string> ClassPresent = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// True from just before a <see cref="AccountVaultStore.TryDepositBatch"/> call records its first
        /// cap claim until that call's phase B has resolved every one of them. While it is set, a re-read
        /// landing mid-batch drops this window's carried PRESENCE but NOT its claims.
        ///
        /// WHY THE ASYMMETRY. A claim is recorded in phase A, and the row it describes is created in phase
        /// B. Between the two the line exists in neither place a cap check can see it: not in the re-read
        /// table, because the credit has not run, and not in the claims, if a reload has just dropped them.
        /// The account is then charged less than it holds and is allowed PAST its cap - the one direction
        /// this rule must never take. Holding the claims instead degrades to the pre-split behaviour for
        /// exactly the items of one in-flight batch: their line may be counted twice once the table is
        /// re-read, which refuses a deposit that would have fit. NEVER UNDER-COUNT is the invariant;
        /// over-counting is survivable and is what this trades for.
        ///
        /// Presence is cleared either way, because presence is only an optimisation - calling a present row
        /// absent costs an AUTO_INCREMENT id, never correctness - and because a re-read makes the table
        /// itself the better oracle.
        ///
        /// Written and read under the store's stateLock, so a reload holding that lock cannot observe it
        /// half-set, and a reload that observed it clear has finished its own <see cref="VaultEntryClaims.Clear"/>
        /// before the batch that armed it can add anything.
        /// </summary>
        internal bool ClaimsInFlight;

        /// <summary>Set when a batch in this window left the stack ledger's re-read to the window's close.</summary>
        internal bool DeferredLedgerReload;

        /// <summary>Set when a batch in this window left the class ledger's re-read to the window's close.</summary>
        internal bool DeferredClassReload;

        /// <summary>
        /// Runs the one coalesced re-read. Idempotent, and it never throws: the store's close routine
        /// catches, because this runs from a finally on a delivery path that is already deciding who owns
        /// which item and must not have that decision replaced by a reload's exception.
        /// </summary>
        public void Dispose()
        {
            if (closed)
                return;

            closed = true;

            close(this);
        }
    }

    /// <summary>
    /// Arms <see cref="VaultDepositWindow.ClaimsInFlight"/> for one batched deposit and disarms it HOWEVER
    /// that call leaves, including by throwing.
    ///
    /// The normal disarm is an explicit <see cref="Disarm"/> the moment phase B has resolved, because that
    /// is the earliest point at which every recorded claim's row provably exists and the flag's cost - a
    /// re-read that keeps claims it could have dropped - stops being worth paying. The
    /// <see cref="Dispose"/> is the net for the paths that never reach it: a throw out of phase A's
    /// vault-container tail or out of any of phase B. Without it a window that threw mid-credit would
    /// carry the flag for the rest of its life and over-charge every later deposit in the same delivery.
    /// Both entry points are idempotent, so running them in sequence is the ordinary case rather than a
    /// bug.
    /// </summary>
    internal sealed class VaultClaimScope : IDisposable
    {
        private readonly Action disarm;

        private bool disarmed;

        internal VaultClaimScope(Action disarm)
        {
            this.disarm = disarm;
        }

        /// <summary>Clears the flag now. Safe to call more than once and safe to call before <see cref="Dispose"/>.</summary>
        internal void Disarm()
        {
            if (disarmed)
                return;

            disarmed = true;

            disarm();
        }

        public void Dispose()
        {
            Disarm();
        }
    }

    /// <summary>
    /// The disposable <see cref="AccountVaultStore.BeginDepositWindow"/> hands back when a window is
    /// already open, so a nested caller cannot close the outer one.
    /// </summary>
    internal sealed class VaultNoOpDisposable : IDisposable
    {
        internal static readonly VaultNoOpDisposable Instance = new VaultNoOpDisposable();

        private VaultNoOpDisposable()
        {
        }

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// What became of ONE item of a batched deposit (SPEC-vault-batch-deposit.md section 3).
    ///
    /// THIS RECORD IS THE DUPE-DIRECTION GUARANTEE, not a convenience. Guarantee 1b: no item whose
    /// group's credit committed is ever handed back to the player. The mechanism is that
    /// <see cref="AccountVaultStore.TryDepositBatch"/> writes "credited, do not hand back" into the
    /// record of EVERY item of EVERY committed group BEFORE the destroy phase begins, never as the
    /// destroy loop reaches each item. A throw out of that loop then leaves items k..N credited, not
    /// destroyed, still detached - and their records already say they are the vault's, so the caller's
    /// disposition loop cannot hand a single one of them back. Written as the loop goes, the same throw
    /// would leave them with no recorded success, and the existing per-item rule for that state is
    /// HandBackOrphan (PersonalVendor.cs:680) - a hand-back of up to 512 already-credited items.
    ///
    /// It is a MUTABLE class rather than a struct for the same reason: the array of records is handed
    /// to the caller through the batch's `out` parameter before any work runs, so the caller sees
    /// whatever state the batch reached even when the batch threw. A struct array would still work for
    /// that, but every "mark this group credited" would then be an index write at a call site rather
    /// than a named transition, which is exactly the shape that invites writing it as the loop goes.
    /// </summary>
    internal sealed class VaultDepositOutcome
    {
        internal VaultDepositOutcome(WorldObject item)
        {
            Item = item;
            Commit = VaultDepositCommit.None;
        }

        /// <summary>The item this outcome describes. Null only when the caller supplied a null element.</summary>
        public WorldObject Item { get; }

        /// <summary>
        /// True when the item is THIS VAULT'S and the caller must not hand it back or treat it as
        /// unsold. False does NOT mean "the caller may hand it back" on its own - read
        /// <see cref="Commit"/> as well, exactly as the single-item path's caller reads
        /// <see cref="AccountVaultStore.LastDepositCommit"/> beside its bool.
        /// </summary>
        public bool Deposited { get; private set; }

        /// <summary>
        /// How far this item got past its point of no return, with the same three meanings
        /// <see cref="VaultDepositCommit"/> documents for the single-item path.
        /// </summary>
        public VaultDepositCommit Commit { get; private set; }

        /// <summary>The player-facing refusal, or null when the item was accepted.</summary>
        public string FailReason { get; private set; }

        /// <summary>
        /// Nothing happened and the item is provably still the caller's: a guard refused it, the cap
        /// refused it, or its group was provably Refused by the database.
        /// </summary>
        internal void Refuse(string failReason)
        {
            Deposited = false;
            Commit = VaultDepositCommit.None;
            FailReason = failReason;
        }

        /// <summary>
        /// Nobody knows whether this item's credit landed. Written BEFORE the statement that credits it
        /// runs, so a THROW out of the backend call leaves the item here rather than in
        /// <see cref="Refuse"/>'s arm - the caller must keep it and must not report it refused.
        /// </summary>
        internal void MarkUnknown(string failReason)
        {
            Deposited = false;
            Commit = VaultDepositCommit.Unknown;
            FailReason = failReason;
        }

        /// <summary>
        /// The credit for this item's group COMMITTED. Called for every item of every committed group
        /// before the destroy phase begins - see this type's own remarks for why that ordering is the
        /// whole point of the record.
        /// </summary>
        internal void MarkCredited()
        {
            Deposited = true;
            Commit = VaultDepositCommit.Committed;
            FailReason = null;
        }

        /// <summary>
        /// The item's biota is in one of this account's vault containers. The vault branch commits
        /// inside <c>DepositToVault</c> itself, at its position in the batch, because it mutates a
        /// container and cannot be deferred.
        /// </summary>
        internal void MarkStored()
        {
            Deposited = true;
            Commit = VaultDepositCommit.Committed;
            FailReason = null;
        }
    }

    /// <summary>
    /// One item's decided route, captured in phase A while the item is still alive and BEFORE any
    /// credit runs. Everything the audit row needs is read off the item here, because by the time the
    /// row is written the biota may be one statement away from being destroyed.
    ///
    /// Only items routed to a DEFERRED group get one of these. An item that took the vault-container
    /// branch has already been stored completely by the time phase A moves on, and an item a guard or
    /// the cap refused never had a route.
    /// </summary>
    internal sealed class VaultDepositItemPlan
    {
        public WorldObject Item;

        public uint Wcid;

        /// <summary>Read before the destroy, because the audit row is written after it.</summary>
        public string Name;

        /// <summary>Stack units for a ledger item; always 1 for a class item.</summary>
        public long Units;

        /// <summary>The value this item contributes to its class pool. Meaningless for a ledger item.</summary>
        public long Value;

        /// <summary>Null for a ledger item; the class row's key for a class item.</summary>
        public string ClassKey;
    }

    /// <summary>
    /// One (account_vault_stack, wcid) group: the unit of atomicity of a batched deposit. Its delta is
    /// applied by ONE statement, so all of its units land or none do, and every item that fed it shares
    /// its outcome.
    ///
    /// Built from a DICTIONARY keyed on the wcid, never appended blindly to a list, because the DAO's
    /// distinct-key precondition is a correctness requirement rather than hygiene: MySQL's CASE takes
    /// the first matching WHEN, so a repeated key would drop the second delta AND leave the matched
    /// count equal to the distinct-key count, so no mismatch would fire and the identifying SELECT
    /// would never run - an under-credit with the items destroyed, presenting as a clean success.
    /// </summary>
    internal sealed class VaultLedgerDepositGroup
    {
        public uint Wcid;

        /// <summary>The summed units of every item in this group. Strictly positive by construction.</summary>
        public long Units;

        /// <summary>Indexes into the caller's item list, in the caller's own order.</summary>
        public readonly List<int> ItemIndexes = new List<int>();
    }

    /// <summary>
    /// One (account_vault_class, class key) group. <see cref="VaultLedgerDepositGroup"/>'s counterpart,
    /// with a second column and the seed the row is created from if it turns out to be absent.
    ///
    /// The COUNT delta is strictly positive (one per item) while the VALUE delta is only NON-NEGATIVE,
    /// and that difference is forced by the data rather than chosen: an item's pooled contribution is
    /// its Value clamped at zero, so a legitimately worthless item contributes exactly 0 and a
    /// strictly-positive rule would refuse an entire sale over one worthless item.
    /// </summary>
    internal sealed class VaultClassDepositGroup
    {
        public string ClassKey;

        public uint Wcid;

        /// <summary>How many ITEMS this group carries. Strictly positive by construction.</summary>
        public long CountDelta;

        /// <summary>The summed pooled value. Non-negative by construction, and legitimately zero.</summary>
        public long ValueDelta;

        /// <summary>Required for EVERY key, not only the ones believed new: a key called present can still turn out absent.</summary>
        public AccountVaultClassSeed Seed;

        /// <summary>The payload the class row is described by, carried for the in-memory row this group creates.</summary>
        public VaultItemClassOverrides Overrides;

        /// <summary>Indexes into the caller's item list, in the caller's own order.</summary>
        public readonly List<int> ItemIndexes = new List<int>();
    }
}
