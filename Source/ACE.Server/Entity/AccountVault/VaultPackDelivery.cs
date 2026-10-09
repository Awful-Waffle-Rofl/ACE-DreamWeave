using System;
using System.Collections.Generic;

using ACE.Server.WorldObjects;

namespace ACE.Server.Entity.AccountVault
{
    /// <summary>
    /// The three distinguishable outcomes of trying to return a withdrawn object to the vault. A
    /// bare bool cannot say all three, and collapsing Threw into Refused would assert a state
    /// (the item is in a known, safe place) that a throw specifically means is no longer known.
    /// </summary>
    internal enum VaultReturnOutcome
    {
        /// <summary>The object landed back in the vault under a known, correct state.</summary>
        Returned,

        /// <summary>
        /// A clean refusal (or the store had already been retired) - the vault declined it, or the
        /// call never ran, but nothing threw and the item's state is fully known: still an
        /// unparented WorldObject the caller holds, never delivered anywhere.
        /// </summary>
        Refused,

        /// <summary>
        /// The underlying call threw. See <see cref="VaultPackDelivery.TryReturnWithdrawn"/>'s remarks -
        /// the vault's state may be INCONSISTENT (a credit or an inventory mutation may have landed
        /// before the throw), so this must be treated as "needs reconciliation", never as a plain
        /// failure, and the same item must NEVER be retried - see the remarks for the double-credit
        /// risk that creates.
        /// </summary>
        Threw,
    }

    /// <summary>Where a <see cref="VaultPackDelivery.WithdrawToPack"/> call left the entry.</summary>
    internal enum VaultPackDeliveryStatus
    {
        /// <summary>
        /// The withdrawn object is with the player: in the pack (<see cref="VaultPackDeliveryResult.DeliveredToPack"/>),
        /// or taken by the caller's <c>tryDeliverFirst</c> step.
        /// </summary>
        Delivered,

        /// <summary>
        /// An object was withdrawn but could not be delivered, and it is back in the vault. Also the
        /// status when TryWithdraw threw after producing an object and that object was returned
        /// (<see cref="VaultPackDeliveryResult.WithdrawException"/> is then set).
        /// </summary>
        Returned,

        /// <summary>
        /// Nothing moved. Either the store refused the work outright
        /// (<see cref="VaultPackDeliveryResult.StoreUnavailable"/>), or TryWithdraw refused cleanly
        /// (<see cref="VaultPackDeliveryResult.FailReason"/>, which the store may leave null).
        /// </summary>
        Refused,

        /// <summary>
        /// An object was withdrawn, could not be delivered, and the vault cleanly REFUSED to take it
        /// back. The object is unparented and is in neither the vault nor the player's possession -
        /// manual recovery required. Kept apart from <see cref="Refused"/> because the two describe
        /// opposite states of the item.
        /// </summary>
        Stranded,

        /// <summary>
        /// The vault's state is unknown: TryWithdraw threw with no object recovered, or the return of
        /// an undeliverable object threw. Needs manual reconciliation against account_vault_log, and
        /// the entry must NEVER be retried (see <see cref="VaultPackDelivery.TryReturnWithdrawn"/>).
        /// </summary>
        Threw,
    }

    /// <summary>The typed result of <see cref="VaultPackDelivery.WithdrawToPack"/>. Carries data only; wording is the caller's.</summary>
    internal sealed class VaultPackDeliveryResult
    {
        public VaultPackDeliveryStatus Status { get; private set; }

        /// <summary>True when the store refused to run the withdraw at all (retired by the idle sweep). Nothing ran.</summary>
        public bool StoreUnavailable { get; private set; }

        /// <summary>TryWithdraw's own refusal reason on a clean refusal. May be null.</summary>
        public string FailReason { get; private set; }

        /// <summary>The exception TryWithdraw threw, when it threw. Not logged here.</summary>
        public Exception WithdrawException { get; private set; }

        /// <summary>
        /// The outcome of the return-to-vault, set only when one was attempted: after TryWithdraw threw
        /// having already produced an object, or after delivery failed.
        /// </summary>
        public VaultReturnOutcome? ReturnOutcome { get; private set; }

        /// <summary>The exception the return-to-vault threw, when it threw. Not logged here.</summary>
        public Exception ReturnException { get; private set; }

        /// <summary>The withdrawn object (the first one TryWithdraw produced), when there was one.</summary>
        public WorldObject Item { get; private set; }

        /// <summary>True when <see cref="VaultPackDeliveryStatus.Delivered"/> came from the pack delivery rather than <c>tryDeliverFirst</c>.</summary>
        public bool DeliveredToPack { get; private set; }

        /// <summary>
        /// Every object TryWithdraw produced BEYOND the first, each already sent back through
        /// <see cref="VaultPackDelivery.TryReturnWithdrawn"/> with its own outcome. Empty in the normal
        /// case. Non-empty means the caller asked for more than one object's worth (a ledger amount above
        /// one stack, a group amount above one): only <see cref="Item"/> was delivered, and a caller that
        /// promised exactly one object must report the extras rather than assume they never existed.
        /// </summary>
        public IReadOnlyList<VaultExtraReturn> ExtraReturns { get; private set; } = Array.Empty<VaultExtraReturn>();

        private VaultPackDeliveryResult() { }

        internal VaultPackDeliveryResult WithExtras(IReadOnlyList<VaultExtraReturn> extras)
        {
            if (extras != null && extras.Count > 0)
                ExtraReturns = extras;

            return this;
        }

        internal static VaultPackDeliveryResult Unavailable() =>
            new VaultPackDeliveryResult { Status = VaultPackDeliveryStatus.Refused, StoreUnavailable = true };

        internal static VaultPackDeliveryResult WithdrawRefused(string failReason) =>
            new VaultPackDeliveryResult { Status = VaultPackDeliveryStatus.Refused, FailReason = failReason };

        internal static VaultPackDeliveryResult Delivered(WorldObject item, bool toPack) =>
            new VaultPackDeliveryResult { Status = VaultPackDeliveryStatus.Delivered, Item = item, DeliveredToPack = toPack };

        internal static VaultPackDeliveryResult WithdrawThrewNothingRecovered(Exception withdrawException) =>
            new VaultPackDeliveryResult { Status = VaultPackDeliveryStatus.Threw, WithdrawException = withdrawException };

        internal static VaultPackDeliveryResult AfterReturn(WorldObject item, VaultReturnOutcome returnOutcome, Exception returnException, Exception withdrawException) =>
            new VaultPackDeliveryResult
            {
                Status = returnOutcome switch
                {
                    VaultReturnOutcome.Returned => VaultPackDeliveryStatus.Returned,
                    VaultReturnOutcome.Refused => VaultPackDeliveryStatus.Stranded,
                    _ => VaultPackDeliveryStatus.Threw,
                },
                Item = item,
                ReturnOutcome = returnOutcome,
                ReturnException = returnException,
                WithdrawException = withdrawException,
            };
    }

    /// <summary>One extra object a withdraw produced and the core sent straight back to the vault.</summary>
    internal sealed class VaultExtraReturn
    {
        public VaultExtraReturn(WorldObject item, VaultReturnOutcome outcome, Exception thrown)
        {
            Item = item;
            Outcome = outcome;
            Thrown = thrown;
        }

        public WorldObject Item { get; }

        public VaultReturnOutcome Outcome { get; }

        /// <summary>The exception the return threw, when <see cref="Outcome"/> is Threw. Not logged here.</summary>
        public Exception Thrown { get; }
    }

    /// <summary>
    /// Withdraws ONE vault entry and delivers it to a player's pack, honouring every clause of
    /// <see cref="AccountVaultStore.TryWithdraw(VaultEntry, int, VaultActor, out List{WorldObject}, out string, GroupTakeOrder)"/>'s
    /// caller contract: an object that comes out is either delivered and saved immediately, or goes
    /// back through TryReturnWithdrawn/TryReturnWithdrawnToLedger - never Destroy()ed.
    ///
    /// Extracted verbatim from the facet restore (Player_Facets.WithdrawAndRestoreFromVault), which is
    /// still its first caller, so any other "move this from the vault to my character" feature runs the
    /// identical item-moving code rather than a second copy of it. Policy - which entries, what to tell
    /// the player, what to log - stays with the caller: this class writes no report line and no log
    /// line, and hands back the exceptions so the caller logs them in its own words.
    ///
    /// THREADING. Fully synchronous on the calling thread. The withdraw and any return run through
    /// <see cref="AccountVaultStore.Enqueue(Action, out Exception)"/>, which does not return until the
    /// work has run (a drain-lock around inline execution - the closure itself may execute on whichever
    /// thread is draining the store at that moment, exactly as before the extraction). The
    /// <c>tryDeliverFirst</c> step and the pack delivery run on the calling thread, outside the queue.
    ///
    /// ONE OBJECT DELIVERED. Only the first object TryWithdraw produces is offered for delivery, exactly
    /// as the facet code it came from. Any further objects (a ledger amount above one stack, a group
    /// amount above one) are NOT delivered and NOT dropped: each goes straight back through
    /// <see cref="TryReturnWithdrawn"/> and is reported in <see cref="VaultPackDeliveryResult.ExtraReturns"/>.
    /// Before that was added the extras were simply unreferenced - out of the vault, in nobody's hands.
    /// The facet caller can never produce more than one (it skips group and class rows and withdraws a
    /// ledger amount of 1), so for it this changes nothing.
    ///
    /// THREE RETURN PATHS, by the entry's source: a CLASS row goes back through
    /// TryReturnWithdrawnToClass (dispatched on <see cref="VaultEntry.Kind"/>, whatever isLedger says),
    /// a ledger row through TryReturnWithdrawnToLedger, a stored item through TryReturnWithdrawn.
    /// </summary>
    internal static class VaultPackDelivery
    {
        /// <summary>
        /// Withdraws <paramref name="amount"/> of <paramref name="entry"/> from <paramref name="store"/>
        /// as <paramref name="player"/> and delivers the object with
        /// <see cref="Player.TryCreateInInventoryWithNetworking(WorldObject)"/> (which saves it).
        ///
        /// <paramref name="isLedger"/> picks the return path for an undeliverable object
        /// (TryReturnWithdrawnToLedger for true, TryReturnWithdrawn for false) and must match the
        /// entry's SOURCE. <paramref name="tryDeliverFirst"/>, when supplied, is offered the withdrawn
        /// object before the pack delivery; returning true means it has taken (and saved) the object
        /// and the pack delivery is skipped. It must send the client nothing about the object when it
        /// returns false, and it must not throw (an exception propagates to the caller with the object
        /// still owed to the vault contract).
        ///
        /// THE ACTOR IS SNAPSHOTTED AT ENTRY. <c>VaultActor.From(player)</c> is taken once here and the
        /// same value is used for the withdraw and for any return, rather than re-read from the player at
        /// return time. It holds the account id, character guid and name.
        ///
        /// NEVER CALL THIS FROM WORK ALREADY RUNNING ON THE STORE'S MUTATION QUEUE. Enqueue runs work
        /// inline when the caller is already on the queue (AccountVaultStore.Enqueue's re-entrancy arm),
        /// and on that arm an exception propagates straight to the caller instead of landing in the
        /// `thrown` out parameter. Every throw-handling branch here - a withdraw that threw after
        /// producing an object, a return that threw - would then be bypassed, and an object TryWithdraw
        /// had already produced would escape the contract with nobody holding it.
        /// </summary>
        internal static VaultPackDeliveryResult WithdrawToPack(AccountVaultStore store, VaultEntry entry, int amount, bool isLedger, Player player,
                                                               Func<WorldObject, bool> tryDeliverFirst = null)
        {
            return WithdrawAndDeliver(store, entry, amount, isLedger, VaultActor.From(player), tryDeliverFirst, player.TryCreateInInventoryWithNetworking);
        }

        /// <summary>
        /// The core of <see cref="WithdrawToPack"/>, with the pack delivery as a parameter so tests can
        /// drive it without a live Player. Production reaches it only through WithdrawToPack.
        /// </summary>
        internal static VaultPackDeliveryResult WithdrawAndDeliver(AccountVaultStore store, VaultEntry entry, int amount, bool isLedger, VaultActor actor,
                                                                   Func<WorldObject, bool> tryDeliverFirst, Func<WorldObject, bool> deliverToPack)
        {
            var ok = false;
            List<WorldObject> withdrawn = null;
            string failReason = null;

            // The two-arg Enqueue, deliberately NOT the one-arg convenience form. Drain catches and
            // swallows a work item's exception without rethrowing, so with the one-arg form a throw
            // partway through TryWithdraw leaves `ok` at its default false while Enqueue still reports
            // true - a THROW misread as a clean REFUSAL. Not theoretical for a withdraw: WithdrawFromLedger
            // debits the ledger and builds the replacement object (populating `withdrawn`) BEFORE its
            // trailing, purely opportunistic DeleteEmptyAccountVaultStacks cleanup call, so a throw in that
            // tail can leave a real, ledger-already-debited object in `withdrawn` with `ok` never set.
            var ran = store.Enqueue(() => ok = store.TryWithdraw(entry, amount, actor, out withdrawn, out failReason), out var thrown);

            if (!ran)
                return VaultPackDeliveryResult.Unavailable();

            if (thrown != null)
            {
                if (withdrawn != null && withdrawn.Count > 0)
                {
                    // TryWithdraw got far enough to build (and, for a ledger withdraw, debit) a real
                    // object before it threw - `ok` never got set, but the object itself is real and
                    // must not be dropped on the floor. Route it back exactly like a failed delivery.
                    // NEVER retry this item after this call, regardless of outcome - see
                    // TryReturnWithdrawn's remarks on the double-credit risk of retrying a Threw item.
                    var recoveredExtras = ReturnExtras(store, entry, isLedger, withdrawn, actor);

                    var recovered = withdrawn[0];
                    var recoveredOutcome = TryReturnWithdrawn(store, entry, isLedger, recovered, actor, out var recoveredReturnThrown);

                    return VaultPackDeliveryResult.AfterReturn(recovered, recoveredOutcome, recoveredReturnThrown, thrown).WithExtras(recoveredExtras);
                }

                return VaultPackDeliveryResult.WithdrawThrewNothingRecovered(thrown);
            }

            if (!ok || withdrawn == null || withdrawn.Count == 0)
                return VaultPackDeliveryResult.WithdrawRefused(failReason);

            // Anything beyond the first object is sent back BEFORE the first is offered for delivery,
            // so no path below (including a throwing tryDeliverFirst) can leave an extra unreferenced.
            var extras = ReturnExtras(store, entry, isLedger, withdrawn, actor);

            var item = withdrawn[0];

            if (tryDeliverFirst != null && tryDeliverFirst(item))
                return VaultPackDeliveryResult.Delivered(item, toPack: false).WithExtras(extras);

            // TryCreateInInventoryWithNetworking also discharges TryWithdraw's caller contract with its
            // own SaveBiotaToDatabase().
            if (!deliverToPack(item))
            {
                // Never delivered - the vault contract requires this go back exactly the way it came
                // out, never destroyed. NEVER retry this item after this call, regardless of outcome -
                // see TryReturnWithdrawn's remarks on the double-credit risk.
                var returnOutcome = TryReturnWithdrawn(store, entry, isLedger, item, actor, out var returnThrown);

                return VaultPackDeliveryResult.AfterReturn(item, returnOutcome, returnThrown, null).WithExtras(extras);
            }

            return VaultPackDeliveryResult.Delivered(item, toPack: true).WithExtras(extras);
        }

        /// <summary>
        /// Sends every object after the first in <paramref name="withdrawn"/> back to the vault, one
        /// TryReturnWithdrawn each, and reports how each went. Never retries: a Threw extra is recorded
        /// and left alone, per TryReturnWithdrawn's remarks.
        /// </summary>
        private static IReadOnlyList<VaultExtraReturn> ReturnExtras(AccountVaultStore store, VaultEntry entry, bool isLedger, List<WorldObject> withdrawn, VaultActor actor)
        {
            if (withdrawn == null || withdrawn.Count <= 1)
                return Array.Empty<VaultExtraReturn>();

            var extras = new List<VaultExtraReturn>(withdrawn.Count - 1);

            for (var i = 1; i < withdrawn.Count; i++)
            {
                var extra = withdrawn[i];
                var outcome = TryReturnWithdrawn(store, entry, isLedger, extra, actor, out var extraThrown);

                extras.Add(new VaultExtraReturn(extra, outcome, extraThrown));
            }

            return extras;
        }

        /// <summary>
        /// Returns a withdrawn-but-undelivered object to the vault it came out of, through the path
        /// matching its SOURCE (TryReturnWithdrawnToLedger for a ledger withdrawal, TryReturnWithdrawn
        /// for a stored item). Uses the two-arg Enqueue(Action, out Exception) overload, not the one-arg
        /// convenience form - the same fix the withdraw call needed, and for the identical reason: the
        /// one-arg form leaves a thrown exception indistinguishable from a clean refusal, because Drain
        /// catches and swallows a work item's exception without rethrowing.
        ///
        /// A throw here is NOT equivalent to a refusal, and the two vault methods this dispatches to
        /// each document their own version of why. TryReturnWithdrawnToLedger's own doc comment names a
        /// throw from ApplyLedgerCount/InvalidateLedger landing AFTER the ledger credit but BEFORE the
        /// object is destroyed - a caller that cannot see the throw does not know the ledger was already
        /// credited, and a caller that then RETRIES the same item double-credits it. TryReturnWithdrawn
        /// (non-ledger) has the same shape: TryAddToInventory has already mutated the vault's in-memory
        /// inventory and set the item's ContainerId before the trailing SaveBiota call can throw, so a
        /// throw there leaves a runtime/DB split, not the clean "never delivered anywhere" state a
        /// Refused result asserts. Either way, the vault's state is unknown afterward, not merely
        /// unchanged - hence Threw is its own outcome rather than folded into Refused.
        ///
        /// The RETURN value itself is also never assumed on a clean call: TryReturnWithdrawn has several
        /// return-false guards (vault capacity chief among them), each commented "the caller still holds
        /// it and must not destroy it", so a caller that assumes success on every call is the Destroy()
        /// the vault's contract forbids, minus the audit trail.
        ///
        /// NO CALLER RETRIES A Threw ITEM, and none may. If a future change ever adds a retry of a failed
        /// delivery, it MUST exclude a Threw item explicitly - retrying it risks double-crediting the
        /// ledger for a ledger-sourced withdrawal, per the remarks above.
        ///
        /// <paramref name="thrown"/> is the exception the return threw, when the outcome is Threw; it is
        /// not logged here.
        /// </summary>
        internal static VaultReturnOutcome TryReturnWithdrawn(AccountVaultStore store, VaultEntry entry, bool isLedger, WorldObject item, VaultActor actor, out Exception thrown)
        {
            var returned = false;

            // A CLASS row is dispatched on the entry's kind, ahead of isLedger, because neither sibling
            // undo is right for it (TryReturnWithdrawnToClass's own remarks): the ledger return would
            // credit the stack ledger with plain template items, and the stored-biota return would move
            // value out of the pooled total. TryReturnWithdrawnToClass answers false (Refused here) for
            // both a clean refusal and an UNKNOWN credit outcome, keeping the object either way; the
            // store logs which one it was.
            var isClass = entry.Kind == VaultEntryKind.Class;

            var ran = store.Enqueue(() =>
            {
                returned = isClass
                    ? store.TryReturnWithdrawnToClass(item, actor)
                    : isLedger
                        ? store.TryReturnWithdrawnToLedger(item, entry.Wcid, item.StackSize ?? 1, actor)
                        : store.TryReturnWithdrawn(item, actor);
            }, out thrown);

            if (!ran)
                return VaultReturnOutcome.Refused;   // store retired - nothing ran, nothing changed

            if (thrown != null)
                return VaultReturnOutcome.Threw;

            return returned ? VaultReturnOutcome.Returned : VaultReturnOutcome.Refused;
        }
    }
}
