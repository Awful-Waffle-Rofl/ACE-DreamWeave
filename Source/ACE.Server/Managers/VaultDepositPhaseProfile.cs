using System;
using System.Diagnostics;

namespace ACE.Server.Managers
{
    /// <summary>
    /// The leaf phases one AccountVaultStore.TryDeposit is split into, in the order the deposit runs them.
    /// Every phase is a LEAF and the phases are DISJOINT, exactly as <see cref="SellPhase"/> is: nothing here
    /// encloses anything else today, so every scope closes through <see cref="VaultDepositPhaseProfile.End"/>,
    /// and <see cref="VaultDepositPhaseProfile.EndExclusive"/> exists for the first phase that does enclose one.
    /// The phases sum to at most the deposit's own measured total, and the difference is reported as one
    /// explicit remainder rather than being spread over the phases.
    ///
    /// The numeric order is load-bearing: <see cref="VaultDepositPhases.TagNames"/> is indexed by it and the tag
    /// names are the vd_&lt;phase&gt;_* keys of the SLOW_TICK log line, so renaming one renames a log field.
    /// Append new phases at the end and keep the tag table in step.
    ///
    /// THREE BRANCHES SHARE THESE NAMES. TryDeposit delegates to TryDepositBatch, which routes every item to
    /// one of three: the STACK LEDGER group (a pristine stackable, which collapses to a wcid ledger row), the
    /// CLASS LEDGER group (a classifiable item, which collapses to a class row - the branch salvage actually
    /// takes), or DepositToVault (everything else, which keeps its biota in a vault container). The first two
    /// are deferred to one batched statement per table; only the third runs at its own item's position. Where
    /// two branches do the same KIND of work the same phase is charged, so
    /// <see cref="Apply"/>, <see cref="Destroy"/> and <see cref="Audit"/> each cover more than one call site and
    /// their counts are per deposit that reached that call site, not per branch. Reading a phase therefore
    /// answers "how much did the shard write cost this tick", never "which branch ran"; the branch-specific
    /// phases (<see cref="RoundTrip"/>, <see cref="FindVault"/>) are what distinguish them.
    ///
    /// SPEC-vault-batch-deposit.md wave 3: <see cref="Upsert"/> is the ONE phase whose counting UNIT changed
    /// with the batch path - see its own remarks - and <see cref="Reload"/> is new, for the batch's phase D
    /// whole-account re-read that replaced every per-item read-back the single-item path used to charge
    /// nowhere at all (it fell into vd_rest_ms).
    /// </summary>
    public enum VaultDepositPhase
    {
        /// <summary>
        /// VaultCollapse.IsPristine, once per deposit, outside every lock. Decides whether the item collapses
        /// to the wcid ledger, and its answer is what the class predicate below is skipped for.
        /// </summary>
        Pristine = 0,

        /// <summary>
        /// The class-description block: VaultItemClass.TryDescribeClass plus VaultItemClass.ClassKey plus
        /// VaultCollapse.ClassDisplayGroupKey, once per deposit, outside every lock.
        ///
        /// Charged once per deposit REGARDLESS of whether the block does anything, because TryDescribeClass
        /// is in the `if` condition and the scope therefore has to wrap the whole statement. So vd_classify_n
        /// equals vd_n, and it is vd_classify_ms that distinguishes a deposit that classified from a pristine
        /// one or one with class storage off, where the short-circuit makes the region near zero.
        /// </summary>
        Classify,

        /// <summary>
        /// The entry-cap decision, once per deposit: the whole lock (stateLock) block, so the LOCK ACQUISITION
        /// is charged here too. That is deliberate - a vault store is contended by the barrel reaper and the
        /// fold migration, and time spent waiting on stateLock is a real cost of the deposit, indistinguishable
        /// from the work inside it to anyone reading a tick that was slow.
        /// </summary>
        Cap,

        /// <summary>
        /// AccountVaultStore.ClassRoundTripHolds, once per deposit that took the class branch. NOT a cheap
        /// predicate: it materializes a REAL WorldObject from the class payload, re-runs TryDescribeClass on
        /// it, re-derives the key, and destroys the probe in a finally. Its own phase precisely because it is
        /// the most expensive thing on the class branch that no earlier measurement had ever separated out.
        /// </summary>
        RoundTrip,

        /// <summary>
        /// AccountVaultStore.FindOrCreateVault, once per deposit that took the vault-container branch. The one
        /// phase with no counterpart in either collapsing branch: it scans the account's vaults for a free slot
        /// and, when there is none, CREATES one (a new container weenie plus its save). Salvage never reaches
        /// it, so vd_findvault_n is 0 on a mule sale and non-zero only when something was stored as a biota.
        /// </summary>
        FindVault,

        /// <summary>
        /// The shard write that credits the deposit: IAccountVaultBackend.TryAdjustAccountVaultStack
        /// (ledger), .TryAdjustAccountVaultClass (class), or the item's own world.SaveBiota (vault
        /// container). These are the three different ways the same commit is made, and they are charged to
        /// one phase so vd_upsert_ms is "what the persistence cost" regardless of which branch the tick
        /// happened to run.
        ///
        /// COUNTING UNIT DIFFERS BY PATH, and this is the one phase where it does. On the single-item path
        /// (TryDeposit, which delegates to TryDepositBatch with a one-element list) a group is always exactly
        /// one item, so once per deposit and once per group coincide, same as before wave 3. On the BATCHED
        /// path (AccountVaultStore.TryDepositBatch called directly, SPEC-vault-batch-deposit.md section 5
        /// step 4) ONE multi-row statement credits every distinct (table, key) GROUP at once, substituting
        /// for what used to be one statement per group's worth of items - so vd_upsert_n is charged once PER
        /// GROUP the statement covers (via <see cref="VaultDepositPhaseProfile.EndForGroups"/>), not once per
        /// statement and not once per item. That is deliberate: vd_upsert_n / vd_n is then exactly the
        /// batch's compression ratio (groups over items), which is unreadable if the phase instead collapsed
        /// to "however many backend calls happened" (at most one per table, always 1 or 2, regardless of how
        /// many groups or items they covered).
        /// </summary>
        Upsert,

        /// <summary>
        /// The in-memory state mutation under stateLock that follows the commit, and the lock acquisition for
        /// it: ApplyClassCountLocked on the class branch, and on the vault branch the whole locked block -
        /// Container.TryAddToInventory, AddStoredItemToGroupingLocked and the dirtyVaults mark. The ledger
        /// branch's equivalent is a single dictionary write, small enough that timing it would cost more than
        /// it measures, so it is left in the remainder.
        /// </summary>
        Apply,

        /// <summary>
        /// world.DestroyItem on the DEPOSITED item, once per collapsing deposit - the ledger and class branches
        /// only. The vault branch keeps the biota and never reaches it, and the class round-trip PROBE's own
        /// destroy is inside <see cref="RoundTrip"/> rather than here, because it is part of what that check
        /// costs.
        /// </summary>
        Destroy,

        /// <summary>
        /// AccountVaultStore.WriteLog, the account_vault_log audit row, once per successful deposit on all
        /// three branches.
        /// </summary>
        Audit,

        /// <summary>
        /// NEW in wave 3 (SPEC-vault-batch-deposit.md section 5 step 7). The batch path's phase D: ONE
        /// whole-account read per table that moved (ledger, class, or both), replacing every per-item
        /// read-back the pre-batch code used to make. Charged once per TryDepositBatch call that reached
        /// phase D, regardless of how many tables reloaded or how many items the batch carried - it is the
        /// one remaining per-sale synchronous round trip, and this phase is what keeps it visible instead of
        /// falling into vd_rest_ms the way it silently did before wave 3. Has no counterpart on the
        /// single-item path before wave 3 landed batching: TryDeposit's own per-item read-backs were never
        /// separately charged, so a 1-item deposit's vd_reload_ms should read close to what an uncharged
        /// read-back used to cost, now finally named rather than absorbed into the remainder.
        /// </summary>
        Reload,
    }

    /// <summary>
    /// Phase names for <see cref="VaultDepositPhase"/>. Each is the middle segment of that phase's SLOW_TICK
    /// keys, vd_&lt;name&gt;_n and vd_&lt;name&gt;_ms. C# identifiers lowercased, so no name can contain a space
    /// or an equals sign and break the logfmt parse.
    /// </summary>
    public static class VaultDepositPhases
    {
        public const int Count = 10;

        public static readonly string[] TagNames =
        {
            "pristine",
            "classify",
            "cap",
            "roundtrip",
            "findvault",
            "upsert",
            "apply",
            "destroy",
            "audit",
            "reload",
        };
    }

    /// <summary>
    /// WaffleACE: per-phase attribution INSIDE AccountVaultStore.TryDeposit, one level below
    /// <see cref="SellPhaseProfile"/>. That instrument names sell_deposit as the culprit for a slow ga_Sell;
    /// this one says which part of the deposit. It exists because four measured sales into a mule vault split
    /// 96% of the whole sell handler into TryDeposit alone (sell_ms 118.48/119.46/103.85/102.21 against
    /// sell_deposit_ms 114.94/115.85/98.79/98.43, four sales of 44/50/44/44 items), so nothing OUTSIDE the
    /// deposit is worth optimizing and nothing inside it had ever been separated out in process.
    ///
    /// THE INVARIANT, identical to SellPhaseProfile's and for the same reason: the reported phases plus ONE
    /// explicit remainder (vd_rest_ms) sum EXACTLY to the deposits' own measured total (vd_ms). Phases are
    /// disjoint leaves (see <see cref="VaultDepositPhase"/>), the remainder is computed as total minus
    /// everything charged rather than being assumed to be zero, and nothing outside a deposit scope can be
    /// charged through <see cref="Begin"/> at all. Without the remainder a phase that was never wired up
    /// looks like a fast one. The ONE region that is charged from outside a deposit scope is a coalescing
    /// window's hoisted phase-D re-read, through <see cref="BeginDeferredReload"/>, which adds its elapsed
    /// time to the deposit TOTAL as well as to its phase so the invariant balances rather than being
    /// excepted - see that method for why a separate seam rather than a weaker guard on Begin.
    ///
    /// THE CROSS-CHECK THIS INSTRUMENT IS FOR: on a tick whose only vault deposits came from a sale, vd_ms
    /// should track sell_deposit_ms closely. The two measure the SAME call from opposite sides -
    /// PersonalVendor.DepositItems wraps the TryDeposit call from its caller (PersonalVendor.cs:629-633), and
    /// TryDeposit times itself from inside - so a divergence means one of the two instruments is wrong, and
    /// that is the point of emitting both. Two divergences are LEGITIMATE and must not be "fixed" by clamping:
    ///
    ///   1. vd_ms EXCEEDS sell_deposit_ms when a sale's deposit closure ran off the world thread. The sell
    ///      instrument then charges nothing (its scope refuses off-thread) and counts the item in
    ///      sell_deposit_unmeasured_n instead - but this instrument is inside TryDeposit, which the closure
    ///      runs on whichever thread won the drain lock, and it refuses off-thread too. So in that case BOTH
    ///      lose the item and the comparison stays honest; what actually makes vd_ms larger is the second case.
    ///   2. vd_ms EXCEEDS sell_deposit_ms when a NON-SALE caller deposited in the same tick.
    ///      VaultMarketItemStore.cs:425 deposits on a market path, and this instrument is armed by TryDeposit
    ///      itself rather than by the sell handler, so that deposit is measured here and is invisible there.
    ///      Read vd_n against sell_deposit_n: equal counts mean the two totals are comparable item for item,
    ///      and a larger vd_n names exactly how many extra deposits vd_ms is carrying.
    ///
    /// vd_ms can also be SMALLER than sell_deposit_ms, always: the sell scope additionally covers the closure
    /// dispatch around TryDeposit, and TryDeposit's own early refusals (a pack, a timed item, a trade note)
    /// return before any phase, which is remainder, not a gap.
    ///
    /// Scope model. <see cref="BeginDeposit"/> opens the one deposit scope and is the ONLY thing that arms the
    /// instrument; <see cref="Begin"/> returns 0 unless a deposit scope is open on this thread. That guard is
    /// what keeps the phases disjoint from anything that is not a deposit: DepositToClass is also called
    /// directly from AccountVaultStore.FoldOne (the fold path, AccountVaultStore.cs:4217, writing
    /// AccountVaultAction.Fold) and from AccountVaultStore.TryReturnWithdrawnToClass (the withdrawn-item
    /// return path, AccountVaultStore.cs:5933, writing AccountVaultAction.Return) WITHOUT going through
    /// TryDeposit, and DepositToVault is reached from DepositToClass's round-trip fallback. None of those may
    /// add time to a total that never contained it. The METHOD NAMES are the durable half of those citations;
    /// the line numbers drift with any edit above them, so re-derive with a grep rather than trusting them.
    ///
    /// Representation: two fixed arrays indexed by <see cref="VaultDepositPhase"/> plus three scalars, reset
    /// per tick by <see cref="BeginPhase"/> alongside <see cref="SellPhaseProfile.BeginPhase"/>, so the
    /// aggregate covers exactly the InboundMessageQueue.RunActions() that WorldTickPhase.InboundMessages times
    /// and the vd_* and sell_* blocks on one line describe the same window. Charges ACCUMULATE per phase: a
    /// 76-item sale reports the same bounded set of fields as a 1-item one, with the count telling them apart.
    ///
    /// Gate: <see cref="InboundOpcodeProfile.Enabled"/>, the same one SellPhaseProfile uses, mirrored from
    /// world_tick_slow_log_enabled once per iteration. Deliberately NO tunable of its own, so the two
    /// instruments can never be half on and their numbers never half comparable.
    ///
    /// Cost when off: <see cref="BeginDeposit"/> reads one static bool and returns 0; every
    /// <see cref="Begin"/> then reads one static bool and returns 0, and every End sees a 0 scope and returns.
    /// No Stopwatch object, no per-item scope object, no allocation on any path, on or off - pinned by
    /// VaultDepositPhaseProfileTests.
    ///
    /// Thread model: world thread only, enforced rather than assumed. A deposit scope opened from any other
    /// thread returns 0, so the barrel reaper's and the fold migration's deposits - which run on their own
    /// threads and can also pick up a sale's queued closure when they win the drain lock - are not measured
    /// rather than corrupting the aggregate. Every field here is written on the world thread only.
    /// </summary>
    public static class VaultDepositPhaseProfile
    {
        private static readonly double ticksPerMs = Stopwatch.Frequency / 1000.0;

        private static readonly long[] phaseTicks = new long[VaultDepositPhases.Count];
        private static readonly int[] phaseCalls = new int[VaultDepositPhases.Count];

        /// <summary>
        /// Running total of every tick charged to any phase this tick. Two jobs: it is the subtrahend
        /// <see cref="EndExclusive"/> uses to make an enclosing phase exclusive of its inner ones, and it is
        /// what the remainder is measured against. Equal to the sum of <see cref="phaseTicks"/> by
        /// construction - both are written only by <see cref="Charge"/>.
        /// </summary>
        private static long chargedTicks;

        private static long depositTicks;
        private static int depositCalls;

        private static bool open;

        /// <summary>Deposits that completed this tick. Zero means the line omits the whole vd block.</summary>
        public static int DepositCalls => depositCalls;

        /// <summary>Total milliseconds those deposits spent, measured by this instrument rather than inferred.</summary>
        public static double DepositMs => depositTicks / ticksPerMs;

        /// <summary>
        /// The UNATTRIBUTED remainder: deposit total minus everything charged to a phase. Signed on purpose.
        /// A positive value is the normal case and names how much of TryDeposit no phase covers - the early
        /// guards, the access check, the branch dispatch, the ledger dictionary write. A NEGATIVE value is
        /// impossible through the production call sites (phases are disjoint and can only be charged inside a
        /// deposit scope) and therefore means the instrumentation itself is miswired, so it is reported rather
        /// than clamped away.
        /// </summary>
        public static double RemainderMs => (depositTicks - chargedTicks) / ticksPerMs;

        public static int CallsAt(VaultDepositPhase phase) => phaseCalls[(int)phase];

        public static double TotalMsAt(VaultDepositPhase phase) => phaseTicks[(int)phase] / ticksPerMs;

        /// <summary>
        /// Ticks charged to phases so far. Read immediately before an enclosing region and handed back to
        /// <see cref="EndExclusive"/>; opaque otherwise.
        /// </summary>
        public static long ChargedTicks => chargedTicks;

        /// <summary>
        /// Clears the per-tick aggregate. Called from WorldManager.UpdateWorld immediately before the
        /// InboundMessageQueue.RunActions() that WorldTickPhase.InboundMessages times, next to
        /// <see cref="SellPhaseProfile.BeginPhase"/>. Unconditional (two 9-element clears and four scalar
        /// stores) for the same reason that one is: a tick that ran with the capture off must not leave stale
        /// numbers behind for the next emit to read. Also disarms the deposit scope, so a scope somehow left
        /// open by an escaping exception cannot leak into the next tick.
        /// </summary>
        public static void BeginPhase()
        {
            Array.Clear(phaseTicks, 0, phaseTicks.Length);
            Array.Clear(phaseCalls, 0, phaseCalls.Length);

            chargedTicks = 0;
            depositTicks = 0;
            depositCalls = 0;
            open = false;
        }

        /// <summary>
        /// Opens the one deposit scope and arms the phase scopes. Returns the start timestamp, or 0 when the
        /// capture is off, when this is not the world thread, or when a scope is somehow already open - in
        /// which case <see cref="EndDeposit"/> does nothing and no phase can be charged. Must be paired with
        /// EndDeposit in a finally, so a throw out of TryDeposit (the DEPOSIT STATE UNKNOWN path is a real one)
        /// cannot leave the instrument armed for whatever the world thread runs next.
        /// </summary>
        public static long BeginDeposit()
        {
            if (open || !InboundOpcodeProfile.Enabled || !WorldTickProfile.IsWorldThread)
                return 0;

            open = true;

            return Stopwatch.GetTimestamp();
        }

        /// <summary>
        /// Closes the deposit scope, disarms the phase scopes and adds this deposit's total to the aggregate.
        /// Inert for a 0 scope, which is also why a nested BeginDeposit (there is none today - no deposit path
        /// reaches TryDeposit again) could not disarm the outer one.
        /// </summary>
        public static void EndDeposit(long started)
        {
            if (started == 0)
                return;

            open = false;

            var elapsed = Stopwatch.GetTimestamp() - started;

            if (elapsed < 0)
                elapsed = 0;

            depositTicks += elapsed;
            depositCalls++;
        }

        /// <summary>
        /// <see cref="EndDeposit"/>'s counterpart for a call to AccountVaultStore.TryDepositBatch made
        /// DIRECTLY (PersonalVendor.DepositItems, wave 3: one Enqueue for a whole sale's items rather than
        /// one per item). Paired with the SAME <see cref="BeginDeposit"/> - the two calls need no separate
        /// "begin batch" seam, because opening the scope does not depend on how many items the call covers.
        ///
        /// ONE physical call now covers <paramref name="itemCount"/> items, so advancing DepositCalls by the
        /// usual one (what EndDeposit does) would make vd_n a CALL count - 1 per sale, however many items it
        /// carried - collapsing the very item-level signal vd_n exists to carry and breaking the reading
        /// "vd_upsert_n / vd_n is the batch's compression ratio" (both sides would then be near-meaningless).
        /// This adds the elapsed time to the deposit total exactly as EndDeposit does, but advances
        /// DepositCalls by <paramref name="itemCount"/> instead, so vd_n stays an ITEM count on the batch
        /// path exactly as it always was on the single-item one.
        ///
        /// Inert for a 0 scope or a non-positive itemCount (mirrors EndDeposit's 0-scope guard; a batch
        /// scope is only ever opened for a non-empty item list).
        /// </summary>
        public static void EndBatchDeposit(long started, int itemCount)
        {
            if (started == 0 || itemCount <= 0)
                return;

            open = false;

            var elapsed = Stopwatch.GetTimestamp() - started;

            if (elapsed < 0)
                elapsed = 0;

            depositTicks += elapsed;
            depositCalls += itemCount;
        }

        /// <summary>
        /// Opens a scope for a phase-D re-read that a coalescing deposit window HOISTED out of the deposits it
        /// belongs to (AccountVaultStore.CloseDepositWindow). It runs after the last deposit's scope has closed,
        /// so <see cref="Begin"/> would return 0 for it and the one remaining synchronous round trip of a whole
        /// market delivery would be charged nowhere - the exact invisibility <see cref="VaultDepositPhase.Reload"/>
        /// was added to end.
        ///
        /// THE INVARIANT STILL HOLDS EXACTLY, which is why this is a separate seam rather than a relaxation of
        /// <see cref="Begin"/>'s guard. <see cref="EndDeferredReload"/> adds the elapsed time to the deposit
        /// TOTAL as well as to the phase, so "phases plus remainder equals total" balances by construction; the
        /// remainder is unchanged, vd_ms grows by exactly what the re-read cost, and vd_n is untouched, so it
        /// stays an ITEM count. Nothing here can be reached from a phase scope: it returns 0 while a deposit
        /// scope is open, precisely so a future caller cannot double-charge time that <see cref="End"/> already
        /// covers.
        ///
        /// Same gate and same thread rule as everything else here: off, or off the world thread, it returns 0.
        /// </summary>
        public static long BeginDeferredReload()
        {
            if (open || !InboundOpcodeProfile.Enabled || !WorldTickProfile.IsWorldThread)
                return 0;

            return Stopwatch.GetTimestamp();
        }

        /// <summary>
        /// Closes a <see cref="BeginDeferredReload"/> scope: charges its elapsed time to
        /// <see cref="VaultDepositPhase.Reload"/> as one call AND adds the same amount to the deposit total, so
        /// the sum-of-phases invariant balances and the remainder is unchanged. See BeginDeferredReload.
        /// </summary>
        public static void EndDeferredReload(long started)
        {
            if (started == 0)
                return;

            var elapsed = Stopwatch.GetTimestamp() - started;

            if (elapsed < 0)
                elapsed = 0;

            depositTicks += elapsed;

            Charge(VaultDepositPhase.Reload, elapsed);
        }

        /// <summary>
        /// Opens a phase scope. Returns the start timestamp, or 0 when no deposit scope is open on this thread
        /// - which covers the capture being off, since only <see cref="BeginDeposit"/> arms it.
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
        public static void End(VaultDepositPhase phase, long started)
        {
            if (started == 0)
                return;

            Charge(phase, Stopwatch.GetTimestamp() - started);
        }

        /// <summary>
        /// <see cref="End"/>'s counterpart for a scope that covers ONE multi-row statement standing in for
        /// what used to be <paramref name="groupCount"/> separate statements - see
        /// <see cref="VaultDepositPhase.Upsert"/>'s own remarks for why the batch path needs this rather than
        /// the plain leaf form. Charges the region's whole elapsed time to <paramref name="phase"/> (so the
        /// sum-of-phases invariant is untouched - nothing is double-counted or dropped), but advances that
        /// phase's CALL count by <paramref name="groupCount"/> instead of by one, so &lt;phase&gt;_n reads as
        /// a group count on the path that calls this rather than a statement count.
        /// <paramref name="groupCount"/> is clamped to at least 1 - this scope is only ever opened when there
        /// is at least one group to charge, and a 0 or negative count must not make the phase's call count
        /// silently go backwards.
        /// </summary>
        public static void EndForGroups(VaultDepositPhase phase, long started, int groupCount)
        {
            if (started == 0)
                return;

            Charge(phase, Stopwatch.GetTimestamp() - started, groupCount);
        }

        /// <summary>
        /// Closes an ENCLOSING phase scope and charges its elapsed time MINUS whatever inner phases charged
        /// while it was open, so the result is that region's own exclusive cost and the phases stay disjoint.
        /// <paramref name="chargedAtStart"/> is <see cref="ChargedTicks"/> read immediately after
        /// <see cref="Begin"/>. No production call site needs it today - every phase in
        /// <see cref="VaultDepositPhase"/> is a leaf - and it is here so that the first one that does cannot
        /// be wired as a leaf and silently double-count, which is the failure SellPhaseProfile's own tests
        /// pin with a control.
        /// </summary>
        public static void EndExclusive(VaultDepositPhase phase, long started, long chargedAtStart)
        {
            if (started == 0)
                return;

            var elapsed = Stopwatch.GetTimestamp() - started;

            Charge(phase, elapsed - (chargedTicks - chargedAtStart));
        }

        /// <summary>
        /// Charges one region to a phase, as exactly one call. Internal rather than private only so
        /// ACE.Server.Tests can drive the aggregation with synthetic tick amounts (InternalsVisibleTo,
        /// ACE.Server.csproj), the same seam SellPhaseProfile.Charge provides; production reaches it only
        /// through <see cref="End"/> and <see cref="EndExclusive"/>. A negative amount - a non-monotonic
        /// timestamp, or an exclusive region whose inner charges rounded past its own elapsed - is clamped to
        /// zero rather than subtracted, so no total can run backwards. Delegates to the count-aware overload
        /// with count 1, so this and <see cref="EndForGroups"/>'s charge share one code path.
        /// </summary>
        internal static void Charge(VaultDepositPhase phase, long elapsedTicks)
        {
            Charge(phase, elapsedTicks, 1);
        }

        /// <summary>
        /// <see cref="Charge"/>'s count-aware form: charges <paramref name="elapsedTicks"/> to
        /// <paramref name="phase"/> exactly once (so the ms total is unaffected by the count), but advances
        /// that phase's CALL count by <paramref name="count"/> rather than by one. See
        /// <see cref="EndForGroups"/>, the only production caller of a count other than 1.
        /// </summary>
        internal static void Charge(VaultDepositPhase phase, long elapsedTicks, int count)
        {
            if (elapsedTicks < 0)
                elapsedTicks = 0;

            if (count < 1)
                count = 1;

            var i = (int)phase;

            phaseTicks[i] += elapsedTicks;
            phaseCalls[i] += count;

            chargedTicks += elapsedTicks;
        }

        /// <summary>
        /// Adds one synthetic deposit total. Test seam only, paired with <see cref="Charge"/>; production
        /// reaches the same fields through <see cref="EndDeposit"/>.
        /// </summary>
        internal static void ChargeDeposit(long elapsedTicks)
        {
            if (elapsedTicks < 0)
                elapsedTicks = 0;

            depositTicks += elapsedTicks;
            depositCalls++;
        }
    }
}
