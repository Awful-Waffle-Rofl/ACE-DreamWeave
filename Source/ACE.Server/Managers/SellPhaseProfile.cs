using System;
using System.Diagnostics;

namespace ACE.Server.Managers
{
    /// <summary>
    /// The leaf phases one player-to-vendor sell handler is split into, in the order the handler runs them.
    /// Every phase is a LEAF and the phases are DISJOINT: a phase that encloses another (Validate encloses
    /// Access and Accept; Purchase encloses Deposit and Panel) is closed with
    /// <see cref="SellPhaseProfile.EndExclusive"/>, which subtracts whatever the inner phases charged while it
    /// was open. So the phases never double-count, they sum to at most the handler's own measured total, and
    /// the difference is reported as one explicit remainder rather than being spread over the phases.
    ///
    /// The numeric order is load-bearing: <see cref="SellPhases.TagNames"/> is indexed by it and the tag names
    /// are the sell_&lt;phase&gt;_* keys of the SLOW_TICK log line, so renaming one renames a log field.
    /// Append new phases at the end and keep the tag table in step.
    /// </summary>
    public enum SellPhase
    {
        /// <summary>
        /// Player_Commerce.VerifySellItems, EXCLUDING <see cref="Access"/> and <see cref="Accept"/>: the
        /// GetAllPossessions dictionary build plus the per-profile possession, duplicate, amount and
        /// IsAcceptableToSell gates. One call per sale.
        /// </summary>
        Validate = 0,

        /// <summary>
        /// Vendor.TryResolveSellAccess, once per sale. A plain vendor answers from a constant; a
        /// PersonalVendor resolves vault authorization, which for a NON-OWNING actor is a shard SELECT.
        /// </summary>
        Access,

        /// <summary>
        /// Vendor.CanAccept, once per candidate item. The base vendor's is a no-op pass; PersonalVendor's
        /// reaches CanAcceptCore, which is the non-mutating deposit pre-flight (and reaches
        /// VaultCollapse.IsPristine by its own admission).
        /// </summary>
        Accept,

        /// <summary>
        /// Per accepted item: TryRemoveFromInventoryWithNetworking / TryDequipObjectWithNetworking (detach,
        /// networking, deferred-save collection) plus the GameEventItemServerSaysContainId that follows a
        /// successful removal. Charged for a FAILED removal too, so the count is items attempted.
        /// </summary>
        Remove,

        /// <summary>AnalyticsManager.RecordVendorSell, once per successfully removed item.</summary>
        Analytics,

        /// <summary>
        /// The one batched FlushDeferredSaves for the whole removal loop. One call per sale (per sale that
        /// got as far as the loop).
        /// </summary>
        Flush,

        /// <summary>
        /// Vendor.ProcessItemsForPurchase, EXCLUDING <see cref="Deposit"/> and <see cref="Panel"/>. For a
        /// plain vendor that is the resell-or-destroy decision and RemoveBiotaFromDatabase per item; for a
        /// PersonalVendor it is DepositItems minus the deposits themselves - the per-item
        /// AccountVaultStore.Enqueue/Drain overhead, trade-note banking and any hand-back.
        /// </summary>
        Purchase,

        /// <summary>
        /// AccountVaultStore.TryDeposit, once per item, measured inside the queued work item so it is
        /// comparable with the out-of-process probe that measured TryDeposit alone. Zero for every vendor
        /// that is not a PersonalVendor.
        ///
        /// The one phase whose count can be LOWER than the number of items processed: the work item does not
        /// always run on the world thread, and then no charge is made. Read sell_deposit_unmeasured_n
        /// alongside it - see <see cref="SellPhaseProfile.NoteDepositExecuted"/>.
        /// </summary>
        Deposit,

        /// <summary>
        /// The coin payout leg: the RecordVendorPayment row and the bank-credit or coin-stack branch. Skipped
        /// entirely for a PersonalVendor, which pays nothing, so a deposit reports payout_n=0.
        /// </summary>
        Payout,

        /// <summary>
        /// The vendor panel rebuild after the sale: the ApproachVendor(player, VendorType.Sell) call at the
        /// tail of ProcessItemsForPurchase, virtual, so PersonalVendor's override (authorization, the rot
        /// restamp and the full GameEventApproachVendor serialization) is inside it.
        /// </summary>
        Panel,
    }

    /// <summary>
    /// Phase names for <see cref="SellPhase"/>. Each is the middle segment of that phase's SLOW_TICK keys,
    /// sell_&lt;name&gt;_n and sell_&lt;name&gt;_ms. C# identifiers lowercased, so no name can contain a space
    /// or an equals sign and break the logfmt parse.
    /// </summary>
    public static class SellPhases
    {
        public const int Count = 10;

        public static readonly string[] TagNames =
        {
            "validate",
            "access",
            "accept",
            "remove",
            "analytics",
            "flush",
            "purchase",
            "deposit",
            "payout",
            "panel",
        };
    }

    /// <summary>
    /// WaffleACE: per-phase attribution INSIDE the player-to-vendor sell game action, one level below
    /// <see cref="InboundOpcodeProfile"/>. That class names ga_Sell as the culprit for a slow
    /// ph_inbound_messages; this one says which part of ga_Sell. It exists because a prod sale of 76 salvage
    /// bags into a mule vault cost 527.62 ms synchronously on the world thread as a single ga_Sell, and an
    /// out-of-process probe of the vault deposit could account for only about a quarter of it - the rest of
    /// the handler had never been measured at all.
    ///
    /// THE INVARIANT, which is the whole point: the reported phases plus ONE explicit remainder
    /// (sell_rest_ms) sum EXACTLY to the handler's own measured total (sell_ms). Phases are disjoint leaves
    /// (see <see cref="SellPhase"/>), the remainder is computed as total minus everything charged rather than
    /// being assumed to be zero, and nothing outside a handler scope can be charged at all. Without the
    /// remainder a phase that was never wired up looks like a fast one - which is exactly the failure that
    /// produced this instrument: the earlier probe's component numbers summed to MORE than its own end-to-end
    /// figure and the inconsistency was caught by chance.
    ///
    /// Scope model. <see cref="BeginHandler"/> opens the one handler scope and is the ONLY thing that arms
    /// the instrument; <see cref="Begin"/> returns 0 unless a handler scope is open on this thread. That guard
    /// is not decoration: ApproachVendor and TryDeposit are both reachable from paths that are not a sale (the
    /// buy handler's panel refresh, a vault command), and charging those would put time in the aggregate that
    /// the handler total does not contain, driving the remainder negative. Being armed only inside the handler
    /// makes the invariant structural instead of a convention.
    ///
    /// Representation: two fixed arrays indexed by <see cref="SellPhase"/> plus four scalars, reset per tick
    /// by <see cref="BeginPhase"/> alongside <see cref="InboundOpcodeProfile.BeginPhase"/>, so the aggregate
    /// covers exactly the InboundMessageQueue.RunActions() that WorldTickPhase.InboundMessages times. Charges
    /// ACCUMULATE per phase rather than being emitted per item: a 200-item sale reports the same bounded set
    /// of fields as a 1-item sale, with the count telling them apart.
    ///
    /// Gate: <see cref="InboundOpcodeProfile.Enabled"/>, which SlowTickReporter mirrors from
    /// world_tick_slow_log_enabled once per iteration. Deliberately NO tunable of its own. Because that
    /// mirror happens at the end of an iteration, the gate cannot change in the middle of a handler.
    ///
    /// Cost when off: <see cref="BeginHandler"/> reads one static bool and returns 0; every
    /// <see cref="Begin"/> then reads one static bool and returns 0, and every End sees a 0 scope and returns.
    /// No Stopwatch object, no per-item scope object, no allocation on any path, on or off - pinned by
    /// SellPhaseProfileTests.
    ///
    /// Thread model: world thread only, enforced rather than assumed, same as InboundOpcodeProfile. A phase
    /// scope opened from any other thread returns 0, so work that reaches an instrumented method off the
    /// world thread is not measured rather than corrupting the aggregate. That covers the easy case - an
    /// instrumented method reached from a path that is not a sale at all - but it is NOT the whole story, and
    /// the harder case has its own counter.
    ///
    /// A SALE'S OWN DEPOSIT CAN EXECUTE ON ANOTHER THREAD. <see cref="SellPhase.Deposit"/> is charged from
    /// inside a closure handed to AccountVaultStore.Enqueue, and that closure does not necessarily run on the
    /// thread that enqueued it. Enqueue appends the item under the store's drainLock and then calls Drain
    /// (AccountVaultStore.cs:2310-2334); Drain executes EVERY item then queued, on whichever thread won the
    /// lock (AccountVaultStore.cs:2419-2484). Two background threads enqueue against the SAME per-account
    /// store - AccountVaultBarrelReaper's hourly sweep (AccountVaultBarrelReaper.cs:216 starts the thread,
    /// :160 enqueues TryPurgeFromBarrel) and AccountVaultFoldMigration (AccountVaultFoldMigration.cs:264,
    /// :499) - so one of them can win the lock in the window between a sale's append and its own Drain call
    /// and run the sale's deposit closure on its thread while the world thread blocks in Drain.
    ///
    /// The sale is unaffected (Enqueue still does not return until the work has run) and the sum invariant
    /// still holds numerically, because the unmeasured time is inside the enclosing ProcessItemsForPurchase
    /// region and therefore lands in <see cref="SellPhase.Purchase"/>. What breaks without the counter is the
    /// attribution: sell_deposit_ms silently undercounts and sell_purchase_ms silently overcounts, on exactly
    /// the phase there is an independent harness number to compare against. A quietly wrong number is worse
    /// than a loudly absent one, which is the same reason the remainder exists - so
    /// <see cref="NoteDepositExecuted"/> counts it and the line emits sell_deposit_unmeasured_n. The fix is
    /// deliberately to make the gap VISIBLE, not to make the profile thread-safe.
    /// </summary>
    public static class SellPhaseProfile
    {
        private static readonly double ticksPerMs = Stopwatch.Frequency / 1000.0;

        private static readonly long[] phaseTicks = new long[SellPhases.Count];
        private static readonly int[] phaseCalls = new int[SellPhases.Count];

        /// <summary>
        /// Running total of every tick charged to any phase this tick. Two jobs: it is the subtrahend
        /// <see cref="EndExclusive"/> uses to make an enclosing phase exclusive of its inner ones, and it is
        /// what the remainder is measured against. Equal to the sum of <see cref="phaseTicks"/> by
        /// construction - both are written only by <see cref="Charge"/>.
        /// </summary>
        private static long chargedTicks;

        private static long handlerTicks;
        private static int handlerCalls;

        /// <summary>
        /// Deposits that a sale enqueued and that produced no <see cref="SellPhase.Deposit"/> charge. See
        /// <see cref="NoteDepositExecuted"/>. Written on the world thread only, like everything else here.
        /// </summary>
        private static int depositUnmeasured;

        private static bool open;

        /// <summary>Sell handlers that completed this tick. Zero means the line omits the whole sell block.</summary>
        public static int HandlerCalls => handlerCalls;

        /// <summary>Total milliseconds those handlers spent, measured by this instrument rather than inferred.</summary>
        public static double HandlerMs => handlerTicks / ticksPerMs;

        /// <summary>
        /// The UNATTRIBUTED remainder: handler total minus everything charged to a phase. Signed on purpose.
        /// A positive value is the normal case and names how much of the handler no phase covers. A NEGATIVE
        /// value is impossible through the production call sites (phases are disjoint and can only be charged
        /// inside a handler scope) and therefore means the instrumentation itself is miswired, so it is
        /// reported rather than clamped away.
        /// </summary>
        public static double RemainderMs => (handlerTicks - chargedTicks) / ticksPerMs;

        /// <summary>
        /// Deposits this tick that ran but were never charged to <see cref="SellPhase.Deposit"/>. A non-zero
        /// value means sell_deposit_ms is an UNDERCOUNT by that many items and sell_purchase_ms carries their
        /// time instead. Nothing else about the line is affected: the sum invariant is untouched, because no
        /// time was added or removed, only attributed to the enclosing phase.
        /// </summary>
        public static int DepositUnmeasured => depositUnmeasured;

        public static int CallsAt(SellPhase phase) => phaseCalls[(int)phase];

        public static double TotalMsAt(SellPhase phase) => phaseTicks[(int)phase] / ticksPerMs;

        /// <summary>
        /// Ticks charged to phases so far. Read immediately before an enclosing region and handed back to
        /// <see cref="EndExclusive"/>; opaque otherwise.
        /// </summary>
        public static long ChargedTicks => chargedTicks;

        /// <summary>
        /// Charges made to <see cref="SellPhase.Deposit"/> so far. Read immediately BEFORE handing a deposit
        /// closure to AccountVaultStore.Enqueue and handed back to <see cref="NoteDepositExecuted"/> once it
        /// returns; opaque otherwise.
        /// </summary>
        public static int DepositCalls => phaseCalls[(int)SellPhase.Deposit];

        /// <summary>
        /// Reconciles one queued deposit against the charge it was supposed to produce, and counts it as
        /// unmeasured when that charge did not land. Called on the world thread AFTER
        /// AccountVaultStore.Enqueue has returned, and only on the path where the work actually ran - a
        /// refused Enqueue ran nothing and there is no deposit to account for.
        /// <paramref name="depositCallsBefore"/> is <see cref="DepositCalls"/> read just before the Enqueue.
        ///
        /// WHY IT IS SHAPED THIS WAY - intent captured at enqueue time on the world thread, outcome checked
        /// at the same place - rather than the obvious alternative of counting from inside the closure when it
        /// notices it is on the wrong thread:
        ///
        /// 1. The closure's thread cannot write here at all. Every field in this class is world-thread-only by
        ///    contract, and a counter incremented from a reaper thread would be the one exception, needing
        ///    Interlocked and still landing in whichever tick's aggregate happened to be current. This way
        ///    there is no foreign write and the thread model stays exactly as documented.
        /// 2. It is race-free without any new synchronization. Enqueue does not return until the work has run
        ///    (AccountVaultStore.cs:2421-2423), and the only writer of the value being compared is this
        ///    thread, so the read after Enqueue cannot see a partial state.
        /// 3. It catches the whole class of cause rather than one instance of it. A foreign drainer is the
        ///    known one, but a throw out of TryDeposit also skips the End (that path is separately logged as
        ///    DEPOSIT STATE UNKNOWN, so the two are distinguishable), and so would any future refactor that
        ///    moved the deposit off this thread. The question answered is "did the charge land", which is the
        ///    question a reader of sell_deposit_ms actually has.
        ///
        /// Inert when no handler scope is open, so it adds nothing to the disabled path beyond the caller's
        /// own <see cref="DepositCalls"/> read.
        /// </summary>
        public static void NoteDepositExecuted(int depositCallsBefore)
        {
            if (!open || !WorldTickProfile.IsWorldThread)
                return;

            if (phaseCalls[(int)SellPhase.Deposit] == depositCallsBefore)
                depositUnmeasured++;
        }

        /// <summary>
        /// Clears the per-tick aggregate. Called from WorldManager.UpdateWorld immediately before the
        /// InboundMessageQueue.RunActions() that WorldTickPhase.InboundMessages times, next to
        /// <see cref="InboundOpcodeProfile.BeginPhase"/>. Unconditional (two 10-element clears and five
        /// scalar stores) for the same reason that one is: a tick that ran with the capture off must not
        /// leave stale numbers behind for the next emit to read. Also disarms the handler scope, so a scope
        /// somehow left open by an escaping exception cannot leak into the next tick.
        /// </summary>
        public static void BeginPhase()
        {
            Array.Clear(phaseTicks, 0, phaseTicks.Length);
            Array.Clear(phaseCalls, 0, phaseCalls.Length);

            chargedTicks = 0;
            handlerTicks = 0;
            handlerCalls = 0;
            depositUnmeasured = 0;
            open = false;
        }

        /// <summary>
        /// Opens the one handler scope and arms the phase scopes. Returns the start timestamp, or 0 when the
        /// capture is off, when this is not the world thread, or when a scope is somehow already open - in
        /// which case <see cref="EndHandler"/> does nothing and no phase can be charged. Must be paired with
        /// EndHandler in a finally, so a throw out of the handler cannot leave the instrument armed.
        /// </summary>
        public static long BeginHandler()
        {
            if (open || !InboundOpcodeProfile.Enabled || !WorldTickProfile.IsWorldThread)
                return 0;

            open = true;

            return Stopwatch.GetTimestamp();
        }

        /// <summary>
        /// Closes the handler scope, disarms the phase scopes and adds this handler's total to the aggregate.
        /// Inert for a 0 scope, which is also why a nested BeginHandler (there is none today) could not
        /// disarm the outer one.
        /// </summary>
        public static void EndHandler(long started)
        {
            if (started == 0)
                return;

            open = false;

            var elapsed = Stopwatch.GetTimestamp() - started;

            if (elapsed < 0)
                elapsed = 0;

            handlerTicks += elapsed;
            handlerCalls++;
        }

        /// <summary>
        /// Opens a phase scope. Returns the start timestamp, or 0 when no handler scope is open on this
        /// thread - which covers the capture being off, since only <see cref="BeginHandler"/> arms it.
        /// </summary>
        public static long Begin()
        {
            if (!open || !WorldTickProfile.IsWorldThread)
                return 0;

            return Stopwatch.GetTimestamp();
        }

        /// <summary>
        /// Closes a LEAF phase scope and charges all of its elapsed time to <paramref name="phase"/>.
        /// </summary>
        public static void End(SellPhase phase, long started)
        {
            if (started == 0)
                return;

            Charge(phase, Stopwatch.GetTimestamp() - started);
        }

        /// <summary>
        /// Closes an ENCLOSING phase scope and charges its elapsed time MINUS whatever inner phases charged
        /// while it was open, so the result is that region's own exclusive cost and the phases stay disjoint.
        /// <paramref name="chargedAtStart"/> is <see cref="ChargedTicks"/> read immediately after
        /// <see cref="Begin"/>. Nesting depth is unbounded and the set of inner phases does not have to be
        /// known here: any charge at all, at any depth, is subtracted.
        /// </summary>
        public static void EndExclusive(SellPhase phase, long started, long chargedAtStart)
        {
            if (started == 0)
                return;

            var elapsed = Stopwatch.GetTimestamp() - started;

            Charge(phase, elapsed - (chargedTicks - chargedAtStart));
        }

        /// <summary>
        /// Charges one region to a phase. Internal rather than private only so ACE.Server.Tests can drive the
        /// aggregation with synthetic tick amounts (InternalsVisibleTo, ACE.Server.csproj), the same seam
        /// InboundOpcodeProfile.Charge provides; production reaches it only through <see cref="End"/> and
        /// <see cref="EndExclusive"/>. A negative amount - a non-monotonic timestamp, or an exclusive region
        /// whose inner charges rounded past its own elapsed - is clamped to zero rather than subtracted, so no
        /// total can run backwards.
        /// </summary>
        internal static void Charge(SellPhase phase, long elapsedTicks)
        {
            if (elapsedTicks < 0)
                elapsedTicks = 0;

            var i = (int)phase;

            phaseTicks[i] += elapsedTicks;
            phaseCalls[i]++;

            chargedTicks += elapsedTicks;
        }

        /// <summary>
        /// Adds one synthetic handler total. Test seam only, paired with <see cref="Charge"/>; production
        /// reaches the same fields through <see cref="EndHandler"/>.
        /// </summary>
        internal static void ChargeHandler(long elapsedTicks)
        {
            if (elapsedTicks < 0)
                elapsedTicks = 0;

            handlerTicks += elapsedTicks;
            handlerCalls++;
        }
    }
}
