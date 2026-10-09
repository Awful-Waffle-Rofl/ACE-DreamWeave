using System;
using System.Diagnostics;
using System.Text;
using System.Threading;

using ACE.Server.Managers;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// WaffleACE: per-phase attribution inside AccountVaultStore.TryDeposit (VaultDepositPhaseProfile).
    ///
    /// THE ACCEPTANCE CRITERION IS THE SUM INVARIANT, the same one SellPhaseProfileTests pins one level up:
    /// the phase totals (ten as of wave 3's Reload phase) plus the one explicit remainder must equal the
    /// deposits' own measured total. The
    /// instrument exists because four measured sales put 96% of the sell handler inside TryDeposit and no part
    /// of that had ever been separated out in process; a phase that was never wired up must therefore be
    /// distinguishable from a fast one, and the remainder is what does that.
    ///
    /// Three kinds of test, mirroring the sell-handler file:
    ///
    /// AGGREGATION, driven through the internal Charge/ChargeDeposit seam with synthetic tick amounts, because
    /// real elapsed time cannot be asserted to the millisecond without a flaky sleep.
    ///
    /// WIRING, driven through the real scope API (BeginDeposit / Begin / End / EndExclusive) over a busy spin,
    /// asserting the invariant on genuinely measured time and - the discriminating one - that work left
    /// OUTSIDE every phase shows up in the remainder instead of being quietly absorbed by a phase.
    ///
    /// GATING, on the two guards that make the invariant structural rather than conventional: nothing is
    /// charged while the capture is off, and nothing is charged outside a deposit scope. The second matters
    /// because DepositToClass and DepositToVault are BOTH reachable without going through TryDeposit - the
    /// fold migration and the barrel return path call DepositToClass directly - and a charge from one of those
    /// would put time in the aggregate that no deposit total contains. Every gating test carries its own
    /// control, so a zero proves the guard fired rather than that the operation never works.
    ///
    /// VaultDepositPhaseProfile is static (it sits inside a world-thread call path and must not be resolved
    /// through an interface), so every test starts from BeginPhase, and TestInitialize claims the world-thread
    /// role by calling WorldTickProfile.BeginIteration - the production gate is exercised as shipped rather
    /// than seamed around. The gate itself is InboundOpcodeProfile.Enabled, shared with SellPhaseProfile on
    /// purpose so the two can never be half on; this class owns it for its duration and restores it in
    /// cleanup, because PropertyManager statics are shared across test classes in one process.
    /// </summary>
    [TestClass]
    public class VaultDepositPhaseProfileTests
    {
        private static readonly double TicksPerMs = Stopwatch.Frequency / 1000.0;

        public TestContext TestContext { get; set; }

        [TestInitialize]
        public void Setup()
        {
            WorldTickProfile.BeginIteration(); // claims this thread as the world thread, as the world loop does
            WorldTickProfile.EndIteration();

            InboundOpcodeProfile.Enabled = true;
            VaultDepositPhaseProfile.BeginPhase();
        }

        [TestCleanup]
        public void Cleanup()
        {
            InboundOpcodeProfile.Enabled = false;
            VaultDepositPhaseProfile.BeginPhase();
        }

        private static long Ticks(double ms) => (long)(ms * TicksPerMs);

        private static double PhaseSumMs()
        {
            var sum = 0.0;

            for (var i = 0; i < VaultDepositPhases.Count; i++)
                sum += VaultDepositPhaseProfile.TotalMsAt((VaultDepositPhase)i);

            return sum;
        }

        /// <summary>
        /// Burns roughly <paramref name="ms"/> milliseconds of wall time without sleeping, so the scopes being
        /// tested measure real elapsed time on this thread. Only used where the assertion is arithmetic (the
        /// sum invariant) or a strict inequality with a wide margin, never a wall-clock equality.
        /// </summary>
        private static void Spin(double ms)
        {
            var until = Stopwatch.GetTimestamp() + Ticks(ms);

            while (Stopwatch.GetTimestamp() < until)
            {
            }
        }

        // ---- aggregation: the sum invariant, through the synthetic seam ----

        [TestMethod]
        public void PhaseTotalsPlusTheRemainderEqualTheDepositTotal()
        {
            VaultDepositPhaseProfile.ChargeDeposit(Ticks(240));

            VaultDepositPhaseProfile.Charge(VaultDepositPhase.Pristine, Ticks(12));
            VaultDepositPhaseProfile.Charge(VaultDepositPhase.Classify, Ticks(75));
            VaultDepositPhaseProfile.Charge(VaultDepositPhase.Cap, Ticks(3));
            VaultDepositPhaseProfile.Charge(VaultDepositPhase.RoundTrip, Ticks(60));
            VaultDepositPhaseProfile.Charge(VaultDepositPhase.Upsert, Ticks(77));
            VaultDepositPhaseProfile.Charge(VaultDepositPhase.Apply, Ticks(1));
            VaultDepositPhaseProfile.Charge(VaultDepositPhase.Destroy, Ticks(6));
            VaultDepositPhaseProfile.Charge(VaultDepositPhase.Audit, Ticks(2));

            Assert.AreEqual(240.0, VaultDepositPhaseProfile.DepositMs, 0.05);
            Assert.AreEqual(236.0, PhaseSumMs(), 0.05);
            Assert.AreEqual(4.0, VaultDepositPhaseProfile.RemainderMs, 0.05, "the remainder must carry the difference, not be assumed zero");

            Assert.AreEqual(VaultDepositPhaseProfile.DepositMs, PhaseSumMs() + VaultDepositPhaseProfile.RemainderMs, 0.01,
                "THE INVARIANT: phases plus the remainder are the deposit total");
        }

        [TestMethod]
        public void AMissingPhaseShowsAsRemainderRatherThanAsAFastPhase()
        {
            // The failure this instrument was built to make impossible: a phase that was never wired up. Here
            // RoundTrip is the un-wired one - the leading suspect for the unexplained time, and the one whose
            // cost would otherwise have looked like zero rather than like a gap.
            VaultDepositPhaseProfile.ChargeDeposit(Ticks(200));
            VaultDepositPhaseProfile.Charge(VaultDepositPhase.Upsert, Ticks(50));

            Assert.AreEqual(0, VaultDepositPhaseProfile.CallsAt(VaultDepositPhase.RoundTrip), "the un-wired phase reports no calls at all");
            Assert.AreEqual(0.0, VaultDepositPhaseProfile.TotalMsAt(VaultDepositPhase.RoundTrip), 0.001);
            Assert.AreEqual(150.0, VaultDepositPhaseProfile.RemainderMs, 0.05, "every unattributed millisecond has to be visible somewhere");
            Assert.AreEqual(VaultDepositPhaseProfile.DepositMs, PhaseSumMs() + VaultDepositPhaseProfile.RemainderMs, 0.01);
        }

        [TestMethod]
        public void ManyDepositsInOneTickAggregateIntoOneSlotWithACallCount()
        {
            // A 76-item sale drives 76 deposits and must emit the same bounded set of fields as a 1-item one.
            for (var i = 0; i < 76; i++)
            {
                VaultDepositPhaseProfile.ChargeDeposit(Ticks(2.4));
                VaultDepositPhaseProfile.Charge(VaultDepositPhase.Upsert, Ticks(0.77));
            }

            Assert.AreEqual(76, VaultDepositPhaseProfile.DepositCalls);
            Assert.AreEqual(76, VaultDepositPhaseProfile.CallsAt(VaultDepositPhase.Upsert));
            Assert.AreEqual(182.4, VaultDepositPhaseProfile.DepositMs, 0.5);
            Assert.AreEqual(58.52, VaultDepositPhaseProfile.TotalMsAt(VaultDepositPhase.Upsert), 0.5);
            Assert.AreEqual(VaultDepositPhaseProfile.DepositMs, PhaseSumMs() + VaultDepositPhaseProfile.RemainderMs, 0.01);
        }

        [TestMethod]
        public void ABackwardsElapsedIsClampedRatherThanSubtracted()
        {
            VaultDepositPhaseProfile.Charge(VaultDepositPhase.Destroy, Ticks(5));
            VaultDepositPhaseProfile.Charge(VaultDepositPhase.Destroy, -Ticks(1000));

            Assert.AreEqual(2, VaultDepositPhaseProfile.CallsAt(VaultDepositPhase.Destroy));
            Assert.AreEqual(5.0, VaultDepositPhaseProfile.TotalMsAt(VaultDepositPhase.Destroy), 0.05, "a non-monotonic timestamp must not drive a total negative");
        }

        [TestMethod]
        public void BeginPhaseClearsTheAggregateEvenWhileDisabled()
        {
            VaultDepositPhaseProfile.ChargeDeposit(Ticks(10));
            VaultDepositPhaseProfile.Charge(VaultDepositPhase.Audit, Ticks(4));

            Assert.AreEqual(1, VaultDepositPhaseProfile.DepositCalls);

            InboundOpcodeProfile.Enabled = false;
            VaultDepositPhaseProfile.BeginPhase();

            Assert.AreEqual(0, VaultDepositPhaseProfile.DepositCalls, "a tick that ran with the capture off must not leave stale numbers for the next emit");
            Assert.AreEqual(0.0, VaultDepositPhaseProfile.DepositMs, 0.001);
            Assert.AreEqual(0.0, VaultDepositPhaseProfile.RemainderMs, 0.001);
            Assert.AreEqual(0, VaultDepositPhaseProfile.CallsAt(VaultDepositPhase.Audit));
            Assert.AreEqual(0.0, PhaseSumMs(), 0.001);
        }

        [TestMethod]
        public void BeginPhaseDisarmsAScopeLeftOpenByAnEscapingException()
        {
            // TryDeposit closes its scope in a finally, so this is belt and braces - but the aggregate must
            // not be corruptible by a leaked scope even so, because a charge from the NEXT tick's work would
            // land against a deposit total this tick already reported.
            var leaked = VaultDepositPhaseProfile.BeginDeposit();

            Assert.AreNotEqual(0L, leaked, "the scope must open, or this test proves nothing");

            VaultDepositPhaseProfile.BeginPhase(); // stands in for the next tick starting

            var stray = VaultDepositPhaseProfile.Begin();

            Assert.AreEqual(0L, stray, "BeginPhase must disarm, or a leaked scope would keep charging into the next tick");

            Spin(1);
            VaultDepositPhaseProfile.End(VaultDepositPhase.Cap, stray);

            Assert.AreEqual(0, VaultDepositPhaseProfile.CallsAt(VaultDepositPhase.Cap));

            // control: a scope opened properly after the reset DOES charge
            var reopened = VaultDepositPhaseProfile.BeginDeposit();

            Assert.AreNotEqual(0L, reopened, "control: BeginPhase must not have broken the instrument, only disarmed it");

            var live = VaultDepositPhaseProfile.Begin();
            VaultDepositPhaseProfile.End(VaultDepositPhase.Cap, live);
            VaultDepositPhaseProfile.EndDeposit(reopened);

            Assert.AreEqual(1, VaultDepositPhaseProfile.CallsAt(VaultDepositPhase.Cap));
        }

        [TestMethod]
        public void EveryPhaseHasATagName()
        {
            Assert.AreEqual(VaultDepositPhases.Count, Enum.GetValues(typeof(VaultDepositPhase)).Length, "a phase without a tag name would render as an out-of-range crash on the emit path");
            Assert.AreEqual(VaultDepositPhases.Count, VaultDepositPhases.TagNames.Length);

            foreach (var name in VaultDepositPhases.TagNames)
            {
                Assert.IsFalse(string.IsNullOrEmpty(name));
                Assert.IsFalse(name.Contains(" ") || name.Contains("="), $"'{name}' would break the logfmt parse");
            }
        }

        // ---- wave 3: the batch-path scope API (EndBatchDeposit, EndForGroups, Reload) ----

        /// <summary>
        /// Step 7 point 1: EndBatchDeposit must add ITEM count to DepositCalls, not one call per
        /// BeginDeposit/EndBatchDeposit pair - the whole reason the batch path needs a different End than
        /// EndDeposit. Also proves EndBatchDeposit adds its elapsed ms to the total exactly like EndDeposit
        /// does, so the sum invariant is unaffected by which End closed the scope.
        /// </summary>
        [TestMethod]
        public void EndBatchDepositAdvancesDepositCallsByItemCountNotByOne()
        {
            var scope = VaultDepositPhaseProfile.BeginDeposit();
            Assert.AreNotEqual(0L, scope, "sanity: the scope must actually open");

            Spin(1);
            VaultDepositPhaseProfile.EndBatchDeposit(scope, 53);

            Assert.AreEqual(53, VaultDepositPhaseProfile.DepositCalls, "one physical batch call covering 53 items must advance vd_n by 53, not by 1");
            Assert.IsTrue(VaultDepositPhaseProfile.DepositMs > 0, "the elapsed time must still be added to the total");
        }

        /// <summary>Control for the test above: a 0 or negative item count must not move DepositCalls at all.</summary>
        [TestMethod]
        public void EndBatchDepositIsInertForANonPositiveItemCount()
        {
            var scope = VaultDepositPhaseProfile.BeginDeposit();
            VaultDepositPhaseProfile.EndBatchDeposit(scope, 0);

            Assert.AreEqual(0, VaultDepositPhaseProfile.DepositCalls);
            Assert.AreEqual(0.0, VaultDepositPhaseProfile.DepositMs, 0.001);
        }

        /// <summary>
        /// Step 7 point 2: EndForGroups must advance the phase's CALL count by the group count while still
        /// charging the region's whole elapsed time as ONE amount - never multiplying it by the group count,
        /// which would inflate vd_upsert_ms and break the sum invariant.
        /// </summary>
        [TestMethod]
        public void EndForGroupsAdvancesTheCallCountByGroupCountButChargesTheElapsedTimeOnce()
        {
            VaultDepositPhaseProfile.BeginDeposit(); // Begin() only charges inside an open deposit scope
            VaultDepositPhaseProfile.ChargeDeposit(Ticks(100));

            var scope = VaultDepositPhaseProfile.Begin();
            Spin(1);
            VaultDepositPhaseProfile.EndForGroups(VaultDepositPhase.Upsert, scope, 7);

            Assert.AreEqual(7, VaultDepositPhaseProfile.CallsAt(VaultDepositPhase.Upsert), "one multi-row statement covering 7 groups must read as 7 upserts, not 1");
            Assert.IsTrue(VaultDepositPhaseProfile.TotalMsAt(VaultDepositPhase.Upsert) > 0, "the statement's real elapsed time must still be charged");

            Assert.AreEqual(VaultDepositPhaseProfile.DepositMs, PhaseSumMs() + VaultDepositPhaseProfile.RemainderMs, 0.01,
                "THE INVARIANT must hold even though one region's CALL count no longer equals 1 - EndForGroups must not double- or under-count the ms side");
        }

        /// <summary>
        /// A single-key batch (or the single-item path, which always carries exactly one group) must still
        /// read as exactly 1, so vd_upsert_n / vd_n is 1 (no compression) in the case where there is genuinely
        /// nothing to compress - the floor EndForGroups' own clamp exists to guarantee.
        /// </summary>
        [TestMethod]
        public void EndForGroupsWithOneGroupMatchesThePlainLeafForm()
        {
            VaultDepositPhaseProfile.BeginDeposit(); // Begin() only charges inside an open deposit scope

            var scope = VaultDepositPhaseProfile.Begin();
            VaultDepositPhaseProfile.EndForGroups(VaultDepositPhase.Upsert, scope, 1);

            Assert.AreEqual(1, VaultDepositPhaseProfile.CallsAt(VaultDepositPhase.Upsert));
        }

        /// <summary>Mirrors EndForGroups' own clamp: a 0 or negative group count must never move the call count backwards - it floors to 1.</summary>
        [TestMethod]
        public void EndForGroupsClampsANonPositiveGroupCountToOne()
        {
            VaultDepositPhaseProfile.BeginDeposit(); // Begin() only charges inside an open deposit scope

            var scope = VaultDepositPhaseProfile.Begin();
            VaultDepositPhaseProfile.EndForGroups(VaultDepositPhase.Upsert, scope, 0);

            Assert.AreEqual(1, VaultDepositPhaseProfile.CallsAt(VaultDepositPhase.Upsert), "a degenerate 0-group call must still count as at least one, never zero or negative");
        }

        /// <summary>
        /// Step 7 point 3: Reload is a phase like any other - it must be chargeable through the ordinary
        /// Begin/End pair and must count toward the sum invariant exactly like every phase that predates it.
        /// </summary>
        [TestMethod]
        public void ReloadIsAChargeablePhaseThatCountsTowardTheInvariant()
        {
            VaultDepositPhaseProfile.ChargeDeposit(Ticks(20));
            VaultDepositPhaseProfile.Charge(VaultDepositPhase.Reload, Ticks(3));

            Assert.AreEqual(1, VaultDepositPhaseProfile.CallsAt(VaultDepositPhase.Reload));
            Assert.AreEqual(3.0, VaultDepositPhaseProfile.TotalMsAt(VaultDepositPhase.Reload), 0.05);
            Assert.AreEqual(17.0, VaultDepositPhaseProfile.RemainderMs, 0.05);
            Assert.AreEqual(VaultDepositPhaseProfile.DepositMs, PhaseSumMs() + VaultDepositPhaseProfile.RemainderMs, 0.01);
        }

        /// <summary>
        /// Compression ratio readability (step 7 point 4), the synthetic version of a 53-item single-key sale:
        /// vd_upsert_n / vd_n must read as 1/53 when one group's statement covers every item.
        /// </summary>
        [TestMethod]
        public void CompressionRatioIsReadableAsUpsertCallsOverDepositCalls()
        {
            var batchScope = VaultDepositPhaseProfile.BeginDeposit();
            var upsertScope = VaultDepositPhaseProfile.Begin();

            VaultDepositPhaseProfile.EndForGroups(VaultDepositPhase.Upsert, upsertScope, 1);
            VaultDepositPhaseProfile.EndBatchDeposit(batchScope, 53);

            Assert.AreEqual(53, VaultDepositPhaseProfile.DepositCalls);
            Assert.AreEqual(1, VaultDepositPhaseProfile.CallsAt(VaultDepositPhase.Upsert));

            var ratio = (double)VaultDepositPhaseProfile.CallsAt(VaultDepositPhase.Upsert) / VaultDepositPhaseProfile.DepositCalls;
            Assert.AreEqual(1.0 / 53.0, ratio, 1e-9, "vd_upsert_n / vd_n must read as the batch's own compression ratio");
        }

        // ---- wiring: the real scope API over real elapsed time ----

        [TestMethod]
        public void RealScopesSumToTheDepositTotalPlusTheRemainder()
        {
            // The production class-branch sequence in miniature: pristine, classify, cap, then DepositToClass'
            // roundtrip / upsert / apply / destroy / audit. Plus unattributed work at the top - the early
            // guards and the access check - which is what the remainder has to carry.
            var deposit = VaultDepositPhaseProfile.BeginDeposit();

            Assert.AreNotEqual(0L, deposit, "the deposit scope must open: enabled, on the world thread");

            Spin(2); // unattributed: CheckReadyLocked, TryGetAccess, the pack/timed/note guards

            foreach (var phase in new[]
            {
                VaultDepositPhase.Pristine,
                VaultDepositPhase.Classify,
                VaultDepositPhase.Cap,
                VaultDepositPhase.RoundTrip,
                VaultDepositPhase.Upsert,
                VaultDepositPhase.Apply,
                VaultDepositPhase.Destroy,
                VaultDepositPhase.Audit,
            })
            {
                var scope = VaultDepositPhaseProfile.Begin();
                Spin(0.5);
                VaultDepositPhaseProfile.End(phase, scope);
            }

            VaultDepositPhaseProfile.EndDeposit(deposit);

            Assert.AreEqual(1, VaultDepositPhaseProfile.DepositCalls);

            Assert.AreEqual(VaultDepositPhaseProfile.DepositMs, PhaseSumMs() + VaultDepositPhaseProfile.RemainderMs, 0.01,
                "THE INVARIANT, on measured time: the nine phases plus the remainder are the deposit total");

            Assert.IsTrue(VaultDepositPhaseProfile.RemainderMs > 0.5,
                $"the 2 ms of work outside every phase must reach the remainder; it was {VaultDepositPhaseProfile.RemainderMs:0.###} ms");

            Assert.AreEqual(0, VaultDepositPhaseProfile.CallsAt(VaultDepositPhase.FindVault),
                "the class branch never reaches FindOrCreateVault, and a phase that did not run must report zero calls rather than be omitted");

            TestContext.WriteLine(
                $"VD_PHASE sum={PhaseSumMs():0.###} rest={VaultDepositPhaseProfile.RemainderMs:0.###} total={VaultDepositPhaseProfile.DepositMs:0.###}");
        }

        [TestMethod]
        public void AnEnclosingPhaseExcludesTheInnerPhasesItContains()
        {
            // No production call site needs EndExclusive today - every phase is a leaf. It is pinned anyway,
            // because the first phase that DOES enclose another must not be wired as a leaf, and this is the
            // test that would catch it.
            var deposit = VaultDepositPhaseProfile.BeginDeposit();

            var outerScope = VaultDepositPhaseProfile.Begin();
            var outerCharged = VaultDepositPhaseProfile.ChargedTicks;

            Spin(1); // the enclosing region's own work

            var inner = VaultDepositPhaseProfile.Begin();
            Spin(8); // the inner region, deliberately much larger
            VaultDepositPhaseProfile.End(VaultDepositPhase.Upsert, inner);

            VaultDepositPhaseProfile.EndExclusive(VaultDepositPhase.RoundTrip, outerScope, outerCharged);
            VaultDepositPhaseProfile.EndDeposit(deposit);

            var innerMs = VaultDepositPhaseProfile.TotalMsAt(VaultDepositPhase.Upsert);
            var outerMs = VaultDepositPhaseProfile.TotalMsAt(VaultDepositPhase.RoundTrip);

            Assert.IsTrue(innerMs > 6, $"the inner phase must have measured its own work; it was {innerMs:0.###} ms");
            Assert.IsTrue(outerMs < innerMs, $"the enclosing phase must not contain the inner one: outer {outerMs:0.###} ms, inner {innerMs:0.###} ms");

            // Control: the same nesting closed as a LEAF double-counts, which is what EndExclusive prevents -
            // so the assertion above pins the subtraction rather than a Spin that never ran.
            VaultDepositPhaseProfile.BeginPhase();

            var deposit2 = VaultDepositPhaseProfile.BeginDeposit();
            var outer2 = VaultDepositPhaseProfile.Begin();

            Spin(1);

            var inner2 = VaultDepositPhaseProfile.Begin();
            Spin(8);
            VaultDepositPhaseProfile.End(VaultDepositPhase.Upsert, inner2);

            VaultDepositPhaseProfile.End(VaultDepositPhase.RoundTrip, outer2); // leaf close: the bug being guarded against
            VaultDepositPhaseProfile.EndDeposit(deposit2);

            Assert.IsTrue(VaultDepositPhaseProfile.TotalMsAt(VaultDepositPhase.RoundTrip) > VaultDepositPhaseProfile.TotalMsAt(VaultDepositPhase.Upsert),
                "control: a leaf close DOES swallow the inner phase's time");
            Assert.IsTrue(VaultDepositPhaseProfile.RemainderMs < 0,
                "control: and the double count drives the remainder negative, which is why a negative is reported rather than clamped");
        }

        [TestMethod]
        public void WorkOutsideEveryPhaseLandsInTheRemainder()
        {
            // Two runs of the same total shape. The second moves one region out of its phase, and the ONLY
            // number that may change is the remainder - the phases must not silently absorb it.
            var depositA = VaultDepositPhaseProfile.BeginDeposit();
            var covered = VaultDepositPhaseProfile.Begin();
            Spin(6);
            VaultDepositPhaseProfile.End(VaultDepositPhase.RoundTrip, covered);
            VaultDepositPhaseProfile.EndDeposit(depositA);

            var restWhenCovered = VaultDepositPhaseProfile.RemainderMs;
            var roundTripWhenCovered = VaultDepositPhaseProfile.TotalMsAt(VaultDepositPhase.RoundTrip);

            VaultDepositPhaseProfile.BeginPhase();

            var depositB = VaultDepositPhaseProfile.BeginDeposit();
            Spin(6); // the same work, now inside no phase at all
            VaultDepositPhaseProfile.EndDeposit(depositB);

            Assert.IsTrue(roundTripWhenCovered > 4, $"the covered run must have charged the phase; it was {roundTripWhenCovered:0.###} ms");
            Assert.IsTrue(restWhenCovered < 2, $"the covered run's remainder must be small; it was {restWhenCovered:0.###} ms");
            Assert.IsTrue(VaultDepositPhaseProfile.RemainderMs > 4, $"the uncovered run's cost must reach the remainder; it was {VaultDepositPhaseProfile.RemainderMs:0.###} ms");
            Assert.AreEqual(0.0, VaultDepositPhaseProfile.TotalMsAt(VaultDepositPhase.RoundTrip), 0.001, "and no phase may have absorbed it");
            Assert.AreEqual(VaultDepositPhaseProfile.DepositMs, PhaseSumMs() + VaultDepositPhaseProfile.RemainderMs, 0.01);
        }

        // ---- gating: off, and outside a deposit scope, nothing may be charged ----

        [TestMethod]
        public void DisabledOpensNoScopeAndRecordsNothing()
        {
            InboundOpcodeProfile.Enabled = false;

            var deposit = VaultDepositPhaseProfile.BeginDeposit();

            Assert.AreEqual(0L, deposit, "the deposit scope must not open while the slow-tick log is off");

            var phase = VaultDepositPhaseProfile.Begin();

            Assert.AreEqual(0L, phase, "and no phase scope may open inside it either");

            Spin(2);

            VaultDepositPhaseProfile.End(VaultDepositPhase.Upsert, phase);
            VaultDepositPhaseProfile.EndDeposit(deposit);

            Assert.AreEqual(0, VaultDepositPhaseProfile.DepositCalls);
            Assert.AreEqual(0, VaultDepositPhaseProfile.CallsAt(VaultDepositPhase.Upsert));
            Assert.AreEqual(0.0, VaultDepositPhaseProfile.DepositMs, 0.001, "End of a 0 scope must be inert, not a charge of (now - 0)");

            // control: the identical sequence with the switch back on does record
            InboundOpcodeProfile.Enabled = true;

            var liveDeposit = VaultDepositPhaseProfile.BeginDeposit();
            var livePhase = VaultDepositPhaseProfile.Begin();

            Assert.AreNotEqual(0L, liveDeposit);
            Assert.AreNotEqual(0L, livePhase);

            Spin(2);

            VaultDepositPhaseProfile.End(VaultDepositPhase.Upsert, livePhase);
            VaultDepositPhaseProfile.EndDeposit(liveDeposit);

            Assert.AreEqual(1, VaultDepositPhaseProfile.DepositCalls);
            Assert.AreEqual(1, VaultDepositPhaseProfile.CallsAt(VaultDepositPhase.Upsert));
        }

        [TestMethod]
        public void APhaseOpenedOutsideAnyDepositScopeIsNotCharged()
        {
            // DepositToClass is called directly from AccountVaultStore.FoldOne (the fold path,
            // AccountVaultStore.cs:4217) and from AccountVaultStore.TryReturnWithdrawnToClass (the
            // withdrawn-item return path, :5933), and DepositToVault is reached from DepositToClass'
            // round-trip fallback. Those paths charge the same phases, and a charge from one of them would
            // put time in the aggregate that no TryDeposit total contains, so Begin has to refuse outside a
            // deposit.
            var strayBefore = VaultDepositPhaseProfile.Begin();

            Assert.AreEqual(0L, strayBefore, "no deposit scope is open, so this must refuse");

            Spin(1);
            VaultDepositPhaseProfile.End(VaultDepositPhase.Audit, strayBefore);

            Assert.AreEqual(0, VaultDepositPhaseProfile.CallsAt(VaultDepositPhase.Audit));

            // And after a deposit has closed, the instrument must be disarmed again.
            var deposit = VaultDepositPhaseProfile.BeginDeposit();

            var inside = VaultDepositPhaseProfile.Begin();
            Assert.AreNotEqual(0L, inside, "control: inside the deposit scope the same call DOES open");
            VaultDepositPhaseProfile.End(VaultDepositPhase.Audit, inside);

            VaultDepositPhaseProfile.EndDeposit(deposit);

            var strayAfter = VaultDepositPhaseProfile.Begin();

            Assert.AreEqual(0L, strayAfter, "EndDeposit must disarm, or a later fold-path DepositToClass would be charged to a sale");
            Assert.AreEqual(1, VaultDepositPhaseProfile.CallsAt(VaultDepositPhase.Audit), "exactly the one charge from inside the scope");
            Assert.IsTrue(VaultDepositPhaseProfile.RemainderMs >= 0, "nothing charged from outside means the remainder cannot go negative");
        }

        /// <summary>
        /// Runs <paramref name="body"/> on a thread that has never called WorldTickProfile.BeginIteration, so
        /// WorldTickProfile.IsWorldThread is false there. An explicit Thread rather than Task.Run, so the work
        /// cannot land back on this thread under any scheduling. Same helper shape as
        /// SellPhaseProfileTests.OnAForeignThread.
        /// </summary>
        private static void OnAForeignThread(Action body)
        {
            Exception thrown = null;

            var t = new Thread(() =>
            {
                try
                {
                    body();
                }
                catch (Exception ex)
                {
                    thrown = ex;
                }
            });

            t.Start();

            Assert.IsTrue(t.Join(TimeSpan.FromSeconds(30)), "the foreign thread did not finish");

            if (thrown != null)
                throw new AssertFailedException("the foreign thread threw", thrown);
        }

        [TestMethod]
        public void OffTheWorldThreadNothingIsCharged()
        {
            // TryDeposit runs inside a queued work item that AccountVaultStore.Drain executes on whichever
            // thread won drainLock - the barrel reaper's and the fold migration's threads both contend for the
            // same store. A deposit that lands on one of them must be LOST rather than charged into the world
            // thread's aggregate, which is also why sell_deposit_unmeasured_n exists one level up.
            var deposit = VaultDepositPhaseProfile.BeginDeposit();

            var onThread = VaultDepositPhaseProfile.Begin();
            Assert.AreNotEqual(0L, onThread, "control: on the world thread, with the deposit open, the scope DOES open");
            VaultDepositPhaseProfile.End(VaultDepositPhase.Upsert, onThread);

            OnAForeignThread(() =>
            {
                Assert.IsTrue(InboundOpcodeProfile.Enabled, "the capture must still be on, or this proves nothing");

                var foreignDeposit = VaultDepositPhaseProfile.BeginDeposit();

                Assert.AreEqual(0L, foreignDeposit, "a TryDeposit run off the world thread must open no deposit scope");

                var foreign = VaultDepositPhaseProfile.Begin();

                Assert.AreEqual(0L, foreign, "and no phase inside it either");

                Spin(1);

                VaultDepositPhaseProfile.End(VaultDepositPhase.Upsert, foreign);
                VaultDepositPhaseProfile.EndDeposit(foreignDeposit);
            });

            VaultDepositPhaseProfile.EndDeposit(deposit);

            Assert.AreEqual(1, VaultDepositPhaseProfile.CallsAt(VaultDepositPhase.Upsert), "only the world thread's charge may be in the aggregate");
            Assert.AreEqual(1, VaultDepositPhaseProfile.DepositCalls, "and only the world thread's deposit may be in the total");
            Assert.AreEqual(VaultDepositPhaseProfile.DepositMs, PhaseSumMs() + VaultDepositPhaseProfile.RemainderMs, 0.01);
        }

        // ---- cost: nothing allocates, on or off ----

        /// <summary>
        /// One synthetic class-branch deposit through the real scope API in the production order, with no work
        /// inside the scopes: what is being measured is the instrument, not the deposit.
        /// </summary>
        private static void DriveOneDeposit()
        {
            var deposit = VaultDepositPhaseProfile.BeginDeposit();

            var pristine = VaultDepositPhaseProfile.Begin();
            VaultDepositPhaseProfile.End(VaultDepositPhase.Pristine, pristine);

            var classify = VaultDepositPhaseProfile.Begin();
            VaultDepositPhaseProfile.End(VaultDepositPhase.Classify, classify);

            var cap = VaultDepositPhaseProfile.Begin();
            VaultDepositPhaseProfile.End(VaultDepositPhase.Cap, cap);

            var roundTrip = VaultDepositPhaseProfile.Begin();
            VaultDepositPhaseProfile.End(VaultDepositPhase.RoundTrip, roundTrip);

            var upsert = VaultDepositPhaseProfile.Begin();
            VaultDepositPhaseProfile.End(VaultDepositPhase.Upsert, upsert);

            var apply = VaultDepositPhaseProfile.Begin();
            VaultDepositPhaseProfile.End(VaultDepositPhase.Apply, apply);

            var destroy = VaultDepositPhaseProfile.Begin();
            VaultDepositPhaseProfile.End(VaultDepositPhase.Destroy, destroy);

            var audit = VaultDepositPhaseProfile.Begin();
            VaultDepositPhaseProfile.End(VaultDepositPhase.Audit, audit);

            VaultDepositPhaseProfile.EndDeposit(deposit);
        }

        [TestMethod]
        public void VaultDepositCaptureAllocatesNothingWhileEnabled()
        {
            for (var i = 0; i < 200 * 76; i++) // warm-up: JIT and tiering
                DriveOneDeposit();

            VaultDepositPhaseProfile.BeginPhase(); // so the counts asserted below are the measured window's alone

            var before = GC.GetAllocatedBytesForCurrentThread();

            for (var i = 0; i < 76_000; i++)
                DriveOneDeposit();

            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.AreEqual(0L, allocated, $"the vault-deposit capture allocated {allocated} bytes over 76,000 deposits");

            // control: the zero above was measured with the capture actually recording, not switched off
            Assert.AreEqual(76_000, VaultDepositPhaseProfile.DepositCalls, "76,000 deposit scopes must have been charged, so recording is what was measured");
            Assert.AreEqual(76_000, VaultDepositPhaseProfile.CallsAt(VaultDepositPhase.RoundTrip));
        }

        [TestMethod]
        public void TheDisabledPathAllocatesNothingAndDoesNoWork()
        {
            for (var i = 0; i < 200 * 76; i++) // warm-up with the capture ON, so the JIT sees the recording path too
                DriveOneDeposit();

            InboundOpcodeProfile.Enabled = false;
            VaultDepositPhaseProfile.BeginPhase();

            for (var i = 0; i < 200 * 76; i++)
                DriveOneDeposit();

            var before = GC.GetAllocatedBytesForCurrentThread();

            for (var i = 0; i < 76_000; i++)
                DriveOneDeposit();

            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.AreEqual(0L, allocated, $"the disabled vault-deposit path allocated {allocated} bytes over 76,000 deposits");

            // The real claim about the disabled path is not "it allocates nothing" but "it does nothing":
            // BeginDeposit returns 0 before touching any state, so no scope opens, no timestamp is taken and
            // no counter moves. That is observable, and it is the assertion that would fail if a future edit
            // moved work above the gate.
            Assert.AreEqual(0, VaultDepositPhaseProfile.DepositCalls, "with the gate off not one of those deposits may have been recorded");
            Assert.AreEqual(0.0, VaultDepositPhaseProfile.DepositMs, 0.001);
            Assert.AreEqual(0.0, PhaseSumMs(), 0.001, "and no phase may have been charged either");
            Assert.AreEqual(0.0, VaultDepositPhaseProfile.RemainderMs, 0.001);
        }

        [TestMethod]
        public void ReportVaultDepositPhaseCost()
        {
            const int deposits = 1_000_000;

            double MeasurePerDepositNs(bool enabled)
            {
                InboundOpcodeProfile.Enabled = enabled;

                for (var i = 0; i < 50_000; i++)
                    DriveOneDeposit();

                var sw = Stopwatch.StartNew();

                for (var i = 0; i < deposits; i++)
                    DriveOneDeposit();

                sw.Stop();

                return sw.Elapsed.TotalMilliseconds * 1_000_000.0 / deposits;
            }

            var on = MeasurePerDepositNs(true);
            var off = MeasurePerDepositNs(false);

            // Reported, not asserted: a wall-clock bound is flaky on a shared runner. The shape to sanity
            // check is that both are a rounding error against a real 2.4 ms deposit.
            TestContext.WriteLine(
                $"VD_PHASE_COST {deposits:N0} deposits: on={on:0.#} ns/deposit, off={off:0.#} ns/deposit (9 scope pairs per deposit)");
        }

        // ---- rendering: the field names and the shape that ships ----

        private static string Render(SlowTickSnapshot s)
        {
            var sb = new StringBuilder();

            SlowTickLine.Append(sb, s);

            return sb.ToString();
        }

        /// <summary>
        /// A 44-item mule sale, the shape the four measured sales had: sell_ms 118.48 with sell_deposit_ms
        /// 114.94 of it, and the vd block splitting that same 114.94 from the inside. The per-phase split is
        /// illustrative, not measured - what the test pins is the field names, the order and the arithmetic.
        ///
        /// A BATCHED sale, as of wave 3's wiring, which is why two of the phase counts are not 44. Upsert is
        /// 2, one per distinct (table, key) GROUP the sale's two multi-row statements covered - the reading
        /// EndForGroups exists to produce, and the number vd_upsert_n / vd_n turns into a compression ratio.
        /// Reload is 1, phase D's single whole-account read per call. Both were zero here until the store was
        /// wired to charge them, and a sample that left them zero would render a field that passes its own
        /// test for the wrong reason: the position would be pinned while the value never moved off the
        /// default an unwired phase also produces.
        /// </summary>
        private static SlowTickSnapshot VaultDepositSample()
        {
            var s = new SlowTickSnapshot();

            s.Iteration = 3092783;
            s.TotalMs = 126.4;
            s.WorldMs = 7.1;
            s.MedianMs = 11.75;
            s.Players = 41;

            s.PhaseMs[(int)WorldTickPhase.InboundMessages] = 119.02;

            s.WorldUpdated = false;

            s.OfferOpcode(InboundOpcodeProfile.ActionKey((int)ACE.Server.Network.GameAction.GameActionType.Sell), 1, 118.9, 118.9);

            s.SellCalls = 1;
            s.SellMs = 118.48;
            s.SellPhaseCalls[(int)SellPhase.Deposit] = 44;
            s.SellPhaseMs[(int)SellPhase.Deposit] = 114.94;
            s.SellPhaseCalls[(int)SellPhase.Panel] = 1;
            s.SellPhaseMs[(int)SellPhase.Panel] = 2.36;
            s.SellPhaseCalls[(int)SellPhase.Remove] = 44;
            s.SellPhaseMs[(int)SellPhase.Remove] = 0.78;

            var sellSum = 0.0;

            for (var i = 0; i < SellPhases.Count; i++)
                sellSum += s.SellPhaseMs[i];

            s.SellRestMs = s.SellMs - sellSum;

            s.VaultDepositCalls = 44;
            s.VaultDepositMs = 114.31;

            s.VaultDepositPhaseCalls[(int)VaultDepositPhase.Pristine] = 44;
            s.VaultDepositPhaseMs[(int)VaultDepositPhase.Pristine] = 6.12;
            s.VaultDepositPhaseCalls[(int)VaultDepositPhase.Classify] = 44;
            s.VaultDepositPhaseMs[(int)VaultDepositPhase.Classify] = 33.08;
            s.VaultDepositPhaseCalls[(int)VaultDepositPhase.Cap] = 44;
            s.VaultDepositPhaseMs[(int)VaultDepositPhase.Cap] = 1.4;
            s.VaultDepositPhaseCalls[(int)VaultDepositPhase.RoundTrip] = 44;
            s.VaultDepositPhaseMs[(int)VaultDepositPhase.RoundTrip] = 30.25;
            s.VaultDepositPhaseCalls[(int)VaultDepositPhase.FindVault] = 0;
            s.VaultDepositPhaseMs[(int)VaultDepositPhase.FindVault] = 0;
            s.VaultDepositPhaseCalls[(int)VaultDepositPhase.Upsert] = 2;
            s.VaultDepositPhaseMs[(int)VaultDepositPhase.Upsert] = 33.88;
            s.VaultDepositPhaseCalls[(int)VaultDepositPhase.Apply] = 44;
            s.VaultDepositPhaseMs[(int)VaultDepositPhase.Apply] = 2.11;
            s.VaultDepositPhaseCalls[(int)VaultDepositPhase.Destroy] = 44;
            s.VaultDepositPhaseMs[(int)VaultDepositPhase.Destroy] = 4.03;
            s.VaultDepositPhaseCalls[(int)VaultDepositPhase.Audit] = 44;
            s.VaultDepositPhaseMs[(int)VaultDepositPhase.Audit] = 1.96;
            s.VaultDepositPhaseCalls[(int)VaultDepositPhase.Reload] = 1;
            s.VaultDepositPhaseMs[(int)VaultDepositPhase.Reload] = 0.92;

            var vdSum = 0.0;

            for (var i = 0; i < VaultDepositPhases.Count; i++)
                vdSum += s.VaultDepositPhaseMs[i];

            s.VaultDepositRestMs = s.VaultDepositMs - vdSum;

            return s;
        }

        [TestMethod]
        public void TheRenderedLineCarriesEveryPhaseAndTheRemainderInAFixedOrder()
        {
            var line = Render(VaultDepositSample());

            Assert.IsFalse(line.Contains("\n"), "one line");

            Assert.IsTrue(line.EndsWith(
                    " vd_n=44 vd_ms=114.31 vd_rest_ms=0.56" +
                    " vd_pristine_n=44 vd_pristine_ms=6.12" +
                    " vd_classify_n=44 vd_classify_ms=33.08" +
                    " vd_cap_n=44 vd_cap_ms=1.4" +
                    " vd_roundtrip_n=44 vd_roundtrip_ms=30.25" +
                    " vd_findvault_n=0 vd_findvault_ms=0" +
                    // 2, not 44: on the batch path this counts GROUPS. vd_upsert_n / vd_n is therefore the
                    // sale's compression ratio (2 statements' worth of keys over 44 items), which is the
                    // whole reason the store closes this scope with EndForGroups rather than End.
                    " vd_upsert_n=2 vd_upsert_ms=33.88" +
                    " vd_apply_n=44 vd_apply_ms=2.11" +
                    " vd_destroy_n=44 vd_destroy_ms=4.03" +
                    " vd_audit_n=44 vd_audit_ms=1.96" +
                    // Wave 3: the new Reload phase is APPENDED, so every key before it keeps its position.
                    // NON-ZERO deliberately - phase D runs once per batched sale, and a sample that left this
                    // at the unwired default would pin the field's position while never proving a charge can
                    // reach it.
                    " vd_reload_n=1 vd_reload_ms=0.92"),
                line);

            Assert.IsTrue(line.IndexOf(" sell_n=", StringComparison.Ordinal) < line.IndexOf(" vd_n=", StringComparison.Ordinal),
                "the vd block is appended AFTER the sell block, so no existing key moves");

            TestContext.WriteLine(line);
        }

        [TestMethod]
        public void TheRenderedPhasesPlusTheRemainderAddUpToTheRenderedTotal()
        {
            // Reads the numbers back OUT of the rendered line and checks the identity there, so the invariant
            // is pinned on what an operator actually parses rather than only on the in-memory fields. The
            // fields are rendered at two decimals, so the epsilon is the rounding of ten of them.
            var line = Render(VaultDepositSample());

            double Field(string key)
            {
                var at = line.IndexOf(" " + key + "=", StringComparison.Ordinal);

                Assert.AreNotEqual(-1, at, $"{key} is missing from the line: {line}");

                var from = at + key.Length + 2;
                var to = line.IndexOf(' ', from);

                var raw = to < 0 ? line.Substring(from) : line.Substring(from, to - from);

                return double.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);
            }

            var sum = Field("vd_rest_ms");

            foreach (var tag in VaultDepositPhases.TagNames)
                sum += Field("vd_" + tag + "_ms");

            Assert.AreEqual(Field("vd_ms"), sum, 0.06,
                "THE INVARIANT as an operator reads it: the ten vd_<phase>_ms plus vd_rest_ms are vd_ms");

            // THE CROSS-CHECK the instrument exists for: the two instruments measure the same call from
            // opposite sides, so on a sale-only tick they must agree item for item and vd_ms must not exceed
            // the outer scope that contains it.
            Assert.AreEqual(Field("sell_deposit_n"), Field("vd_n"),
                "equal counts are what make the two totals comparable item for item");
            Assert.IsTrue(Field("vd_ms") <= Field("sell_deposit_ms"),
                "with no non-sale depositor in the tick, vd_ms measures the inside of the call sell_deposit_ms wraps");
        }

        [TestMethod]
        public void TheVaultDepositBlockIsOmittedEntirelyWhenNoDepositRan()
        {
            var s = VaultDepositSample();

            s.ResetVaultDeposit();

            var line = Render(s);

            Assert.IsFalse(line.Contains("vd_"), $"an iteration with no vault deposit in it must not carry the block at all: {line}");
            Assert.IsTrue(line.Contains(" sell_n=1 "), "control: the sell block it is appended after is still there");
        }

        [TestMethod]
        public void TheVaultDepositBlockSurvivesWithoutASellBlock()
        {
            // A deposit can arrive from a caller that is not a sale (VaultMarketItemStore.cs:425), so the vd
            // block is NOT conditional on the sell block being present - and a reader seeing vd_* with no
            // sell_* knows immediately that the deposits did not come from a vendor sale.
            var s = VaultDepositSample();

            s.ResetSell();

            var line = Render(s);

            Assert.IsFalse(line.Contains("sell_"), $"no sale ran: {line}");
            Assert.IsTrue(line.Contains(" vd_n=44 "), $"but the deposits still have to be reported: {line}");
            Assert.IsTrue(line.EndsWith(" vd_audit_n=44 vd_audit_ms=1.96 vd_reload_n=1 vd_reload_ms=0.92"), line);
        }
    }
}
