using System;
using System.Collections.Generic;

using log4net;

using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.Entity.AccountVault
{
    /// <summary>Where one item ended up after <see cref="VaultPackDeposit.DepositAndDispose"/>.</summary>
    internal enum VaultPackDepositStatus
    {
        /// <summary>The item is the vault's: its credit committed, or it was added to a vault container.</summary>
        Deposited,

        /// <summary>The detach step refused it, so it was never submitted to the store. Still where it was.</summary>
        NotDetached,

        /// <summary>The vault did not take it (provably: Commit None) and it is back with the player.</summary>
        HandedBack,

        /// <summary>
        /// The vault did not take it, and the hand-back to the player ALSO failed. The item is detached, in no
        /// container and undestroyed: manual recovery required.
        /// </summary>
        HandBackFailed,

        /// <summary>
        /// The vault reported it not taken, but the item is nevertheless still parented (in a container or on a
        /// body), so it was NOT handed back - doing that would put one object in two places. The same rule
        /// PersonalVendor.HandBackOrphan applies.
        /// </summary>
        AlreadyParented,

        /// <summary>
        /// The deposit threw and this item's commit is Committed or Unknown, so it is kept from the player:
        /// handing back an item whose credit may have landed is a duplicate. Reconcile against account_vault_log.
        /// </summary>
        Withheld,
    }

    /// <summary>One item's disposition. Data only; wording is the caller's.</summary>
    internal sealed class VaultPackDepositItemResult
    {
        public VaultPackDepositItemResult(WorldObject item, VaultPackDepositStatus status, VaultDepositCommit commit, string failReason)
        {
            Item = item;
            Status = status;
            Commit = commit;
            FailReason = failReason;
        }

        public WorldObject Item { get; }

        public VaultPackDepositStatus Status { get; }

        /// <summary>The store's commit state for the item, or None when it was never submitted or no outcome exists.</summary>
        public VaultDepositCommit Commit { get; }

        /// <summary>The store's refusal reason, or <see cref="AccountVaultStore.UnavailableMessage"/>. Null when deposited.</summary>
        public string FailReason { get; }
    }

    /// <summary>The typed result of one <see cref="VaultPackDeposit.DepositAndDispose"/> call. Carries data only.</summary>
    internal sealed class VaultPackDepositResult
    {
        public VaultPackDepositResult(IReadOnlyList<VaultPackDepositItemResult> items, bool storeUnavailable, Exception thrown)
        {
            Items = items;
            StoreUnavailable = storeUnavailable;
            Thrown = thrown;
        }

        /// <summary>One entry per input item, in input order.</summary>
        public IReadOnlyList<VaultPackDepositItemResult> Items { get; }

        /// <summary>True when no store would run the batch (none, or retired twice). Nothing reached the vault.</summary>
        public bool StoreUnavailable { get; }

        /// <summary>The exception the queued batch threw, when it threw. Not logged here.</summary>
        public Exception Thrown { get; }
    }

    /// <summary>
    /// Deposits a set of items out of a player's pack into the account vault, honouring every clause of
    /// <see cref="AccountVaultStore.TryDepositBatch"/>'s partial-failure contract. The deposit-side twin of
    /// <see cref="VaultPackDelivery"/>, and the same split: this class moves items and reports what happened to
    /// each one, it writes no chat line (and logs only a throwing delegate, below), and the caller words the rest.
    ///
    /// THE RULES ARE COPIED FROM PersonalVendor.DepositItems AND MUST STAY IN STEP WITH IT (a reviewer diffs the
    /// two). That method is the vault's one long-standing batch depositor, and each rule below is one it learned
    /// the hard way:
    ///   - ONE TryDepositBatch call inside ONE Enqueue, never one Enqueue per item;
    ///   - the two-argument <see cref="AccountVaultStore.Enqueue(Action, out Exception)"/>, never the
    ///     one-argument form, because Drain swallows a work item's throw and the one-argument form then reads a
    ///     throw as a clean refusal;
    ///   - Enqueue returning false (the store was retired by the idle sweep) means the work did NOT run, so the
    ///     store is fetched again and the whole batch retried ONCE; a second refusal hands everything back;
    ///   - after a THROW, only an item whose outcome is <c>!Deposited &amp;&amp; Commit == None</c> is handed back.
    ///     None is written by nothing downstream of a credit, so it provably means no credit was attempted;
    ///     Committed and Unknown are withheld, and a null or short outcome list is treated as Unknown;
    ///   - an item that is already parented is never handed back (PersonalVendor.HandBackOrphan's guard);
    ///   - the detach saves are flushed BEFORE the batch runs, the sell path's order
    ///     (Player_Commerce.HandleActionSellItem), so a pack save can never land after the vault's delete of the
    ///     same biota.
    ///
    /// NEVER CALL THIS FROM WORK ALREADY RUNNING ON THE STORE'S MUTATION QUEUE - the same rule as
    /// VaultPackDelivery.WithdrawToPack, for the same reason: Enqueue runs work inline on that arm and an
    /// exception then propagates to the caller instead of landing in the out parameter, bypassing every
    /// throw-handling branch here with items already detached.
    ///
    /// A THROWING DELEGATE STRANDS NOTHING ELSE. Each detach and each hand-back call is guarded on its own: a detach
    /// that throws is NotDetached (never submitted) and a hand-back that throws is HandBackFailed, each logged at
    /// ERROR with the guid, and every later item is still processed. The only log lines this class writes.
    /// </summary>
    internal static class VaultPackDeposit
    {
        private static readonly ILog log = LogManager.GetLogger(typeof(VaultPackDeposit));

        /// <summary>
        /// Production entry. Detaches each item from <paramref name="player"/>'s inventory with
        /// RemoveFromInventoryAction.SpendItem (which sends the client InventoryRemoveObject, the container-cleared
        /// update and the burden update) collecting the off-player saves, flushes them, deposits, and hands back
        /// anything the vault did not take with TryCreateInInventoryWithNetworking (burden check off: the item was
        /// in the pack one call earlier) followed by SaveBiotaToDatabase, as PersonalVendor.HandBackOrphan does.
        /// </summary>
        internal static VaultPackDepositResult DepositFromPack(AccountVaultStore store, IReadOnlyList<WorldObject> items, Player player)
        {
            var actor = VaultActor.From(player);
            var deferredSaves = Player.NewDeferredSaveList();

            return DepositAndDispose(store, () => AccountVaultManager.GetStore(actor.AccountId), items, actor,
                item => player.TryRemoveFromInventoryWithNetworking(item.Guid, out var removed, Player.RemoveFromInventoryAction.SpendItem, deferredSaves) && ReferenceEquals(removed, item),
                () => Player.FlushDeferredSaves(deferredSaves),
                item =>
                {
                    if (!player.TryCreateInInventoryWithNetworking(item, out _, burdenCheck: false))
                        return false;

                    item.SaveBiotaToDatabase();
                    return true;
                });
        }

        /// <summary>
        /// The core of <see cref="DepositFromPack"/>, with every Player-bound step as a parameter so tests can drive
        /// it against a real <see cref="AccountVaultStore"/> over the fakes. Production reaches it only through
        /// DepositFromPack.
        ///
        /// <paramref name="refetchStore"/> is the retry's store source (AccountVaultManager.GetStore in production),
        /// consulted only when Enqueue refuses. <paramref name="detach"/> returns true when the item is off the
        /// player and unparented; <paramref name="handBack"/> returns true when the item is back with the player
        /// and saved. Neither may throw.
        /// </summary>
        internal static VaultPackDepositResult DepositAndDispose(AccountVaultStore store, Func<AccountVaultStore> refetchStore, IReadOnlyList<WorldObject> items, VaultActor actor,
                                                                 Func<WorldObject, bool> detach, Action flushDetachSaves, Func<WorldObject, bool> handBack)
        {
            var count = items?.Count ?? 0;
            var results = new VaultPackDepositItemResult[count];

            // Detach first, every item, and only what detached is ever submitted. An item that did not come off the
            // player is still wherever it was, and submitting it would only earn the store's still-parented refusal.
            var submitted = new List<WorldObject>(count);
            var submittedIndexes = new List<int>(count);

            for (var i = 0; i < count; i++)
            {
                var item = items[i];

                if (item == null || !TryDetach(detach, item))
                {
                    results[i] = new VaultPackDepositItemResult(item, VaultPackDepositStatus.NotDetached, VaultDepositCommit.None, null);
                    continue;
                }

                submitted.Add(item);
                submittedIndexes.Add(i);
            }

            // BEFORE the batch, the sell path's order: the shard queue is strict FIFO, so save-then-delete leaves
            // the row deleted, and the reverse would resurrect a deposited item after the vault's delete.
            flushDetachSaves?.Invoke();

            if (submitted.Count == 0)
                return new VaultPackDepositResult(results, false, null);

            var activeStore = store;

            if (activeStore == null)
            {
                HandBackAll(submitted, submittedIndexes, results, handBack);
                return new VaultPackDepositResult(results, true, null);
            }

            var ok = false;
            IReadOnlyList<VaultDepositOutcome> outcomes = null;

            // Reads `activeStore` through the closure, so the retry below observes the re-fetched store.
            Action work = () => ok = activeStore.TryDepositBatch(submitted, actor, out outcomes);

            if (!activeStore.Enqueue(work, out var thrown))
            {
                // PersonalVendor.DepositItems I8: false means retired by the idle sweep and NOTHING ran, so every
                // item is provably still the caller's. Re-fetch and retry the whole batch once.
                activeStore = refetchStore?.Invoke();

                if (activeStore == null || !activeStore.Enqueue(work, out thrown))
                {
                    HandBackAll(submitted, submittedIndexes, results, handBack);
                    return new VaultPackDepositResult(results, true, null);
                }
            }

            if (thrown != null)
            {
                // PersonalVendor.DepositItems F4: a throw is not a refusal. `outcomes` was assigned before any work
                // ran (TryDepositBatch's own remarks), so it is readable here. Only a provable None goes home.
                for (var k = 0; k < submitted.Count; k++)
                {
                    var item = submitted[k];
                    var outcome = outcomes != null && k < outcomes.Count ? outcomes[k] : null;

                    if (outcome != null && !outcome.Deposited && outcome.Commit == VaultDepositCommit.None)
                    {
                        results[submittedIndexes[k]] = HandBackOne(item, outcome.Commit, outcome.FailReason, handBack);
                        continue;
                    }

                    var commit = outcome == null
                        ? VaultDepositCommit.Unknown
                        : outcome.Deposited ? VaultDepositCommit.Committed : outcome.Commit;

                    results[submittedIndexes[k]] = new VaultPackDepositItemResult(item, VaultPackDepositStatus.Withheld, commit, outcome?.FailReason ?? AccountVaultStore.UnavailableMessage);
                }

                return new VaultPackDepositResult(results, false, thrown);
            }

            // Returned normally: every outcome is final. Deposited is the single authoritative per-item answer, as in
            // DepositItems - false covers Refused and an Unknown-without-a-throw alike, both of which leave the item
            // undestroyed and in need of a hand-back (the store never returns an item to a pack itself).
            for (var k = 0; k < submitted.Count; k++)
            {
                var item = submitted[k];
                var outcome = outcomes[k];

                results[submittedIndexes[k]] = outcome.Deposited
                    ? new VaultPackDepositItemResult(item, VaultPackDepositStatus.Deposited, outcome.Commit, null)
                    : HandBackOne(item, outcome.Commit, outcome.FailReason, handBack);
            }

            return new VaultPackDepositResult(results, false, null);
        }

        private static void HandBackAll(List<WorldObject> submitted, List<int> submittedIndexes, VaultPackDepositItemResult[] results, Func<WorldObject, bool> handBack)
        {
            for (var k = 0; k < submitted.Count; k++)
                results[submittedIndexes[k]] = HandBackOne(submitted[k], VaultDepositCommit.None, AccountVaultStore.UnavailableMessage, handBack);
        }

        /// <summary>PersonalVendor.HandBackOrphan plus TryHandBack, as data: the already-parented guard, then the placement.</summary>
        private static VaultPackDepositItemResult HandBackOne(WorldObject item, VaultDepositCommit commit, string reason, Func<WorldObject, bool> handBack)
        {
            reason ??= AccountVaultStore.UnavailableMessage;

            if (IsAlreadyParented(item))
                return new VaultPackDepositItemResult(item, VaultPackDepositStatus.AlreadyParented, commit, reason);

            bool placed;

            try
            {
                placed = handBack != null && handBack(item);
            }
            catch (Exception ex)
            {
                log.Error($"[VAULT] hand-back of 0x{item?.Guid.Full ?? 0:X8} ({item?.Name}) after a refused pack deposit threw; it is detached and needs manual recovery. Continuing with the remaining items.", ex);
                placed = false;
            }

            return new VaultPackDepositItemResult(item, placed ? VaultPackDepositStatus.HandedBack : VaultPackDepositStatus.HandBackFailed, commit, reason);
        }

        /// <summary>The detach delegate, guarded: a throw is a NotDetached item, logged with its guid, never a stranded batch.</summary>
        private static bool TryDetach(Func<WorldObject, bool> detach, WorldObject item)
        {
            try
            {
                return detach(item);
            }
            catch (Exception ex)
            {
                // If the detach got partway before throwing the item may already be off the player; it is NOT submitted,
                // so the store never takes it, and the ERROR names it for reconciliation.
                log.Error($"[VAULT] detach of 0x{item.Guid.Full:X8} ({item.Name}) for a pack deposit threw; it is not being deposited. Continuing with the remaining items.", ex);
                return false;
            }
        }

        /// <summary>The same test as PersonalVendor.IsAlreadyParented: in a container or on a body.</summary>
        internal static bool IsAlreadyParented(WorldObject item)
        {
            return item != null && (item.ContainerId != null || item.WielderId != null);
        }
    }
}
