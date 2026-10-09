using System;
using System.Diagnostics;
using System.Text;
using System.Threading;

using ACE.Server.Entity.AccountVault;
using ACE.Server.Managers;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// WaffleACE: per-phase attribution inside the player-to-vendor sell handler (SellPhaseProfile).
    ///
    /// THE ACCEPTANCE CRITERION IS THE SUM INVARIANT, and most of this file is about it: the ten phase totals
    /// plus the one explicit remainder must equal the handler's own measured total. The instrument exists
    /// because the previous measurement of this path reported component numbers that summed to MORE than its
    /// own end-to-end figure, and nothing in that report could show it. So there are three kinds of test here:
    ///
    /// AGGREGATION, driven through the internal Charge/ChargeHandler seam with synthetic tick amounts, because
    /// real elapsed time cannot be asserted to the millisecond without a flaky sleep - the same seam
    /// InboundOpcodeProfile.Charge provides.
    ///
    /// WIRING, driven through the real scope API (BeginHandler / Begin / End / EndExclusive) over a busy spin,
    /// asserting the invariant on genuinely measured time, that an enclosing phase is exclusive of its inner
    /// ones, and - the discriminating one - that work left OUTSIDE every phase shows up in the remainder
    /// instead of being quietly absorbed by a phase.
    ///
    /// GATING, on the two guards that make the invariant structural rather than conventional: nothing is
    /// charged while the capture is off, and nothing is charged outside a handler scope. The second matters
    /// because ApproachVendor and TryDeposit are both instrumented and both reachable from paths that are not
    /// a sale; a charge from one of those would exceed a handler total that never contained it. Every gating
    /// test carries its own control, so a zero proves the guard fired rather than that the operation never
    /// works.
    ///
    /// SellPhaseProfile is static (it sits inside a world-thread handler and must not be resolved through an
    /// interface), so every test starts from BeginPhase, and TestInitialize claims the world-thread role by
    /// calling WorldTickProfile.BeginIteration - the production gate is exercised as shipped rather than
    /// seamed around. The gate itself is InboundOpcodeProfile.Enabled, which this class owns for its duration
    /// and restores in cleanup (PropertyManager statics are shared across test classes in one process).
    /// </summary>
    [TestClass]
    public class SellPhaseProfileTests
    {
        private static readonly double TicksPerMs = Stopwatch.Frequency / 1000.0;

        /// <summary>Account id for the contention test's throwaway store. Its own, shared with nothing.</summary>
        private const uint ContendedAccount = 9731;

        public TestContext TestContext { get; set; }

        [TestInitialize]
        public void Setup()
        {
            WorldTickProfile.BeginIteration(); // claims this thread as the world thread, as the world loop does
            WorldTickProfile.EndIteration();

            // Seeded because EveryDepositIsAccountedForWhileAnotherThreadContendsForTheSameStore builds a real
            // AccountVaultStore, and PropertyManager's caches are shared by every test class in the process -
            // a key this class reads but never seeds passes only when some earlier class happened to seed it.
            // Same two keys AccountVaultConcurrencyTests seeds, and for the same reason: an uncached
            // PropertyManager read falls through to DatabaseManager.ShardConfig and opens a ShardDbContext.
            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_entry_cap", 100000));
            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_landblock",
                DefaultPropertyManager.DefaultLongProperties["account_vault_landblock"].Item));

            InboundOpcodeProfile.Enabled = true;
            SellPhaseProfile.BeginPhase();
        }

        [TestCleanup]
        public void Cleanup()
        {
            InboundOpcodeProfile.Enabled = false;
            SellPhaseProfile.BeginPhase();
        }

        private static long Ticks(double ms) => (long)(ms * TicksPerMs);

        private static double PhaseSumMs()
        {
            var sum = 0.0;

            for (var i = 0; i < SellPhases.Count; i++)
                sum += SellPhaseProfile.TotalMsAt((SellPhase)i);

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
        public void PhaseTotalsPlusTheRemainderEqualTheHandlerTotal()
        {
            SellPhaseProfile.ChargeHandler(Ticks(500));

            SellPhaseProfile.Charge(SellPhase.Validate, Ticks(20));
            SellPhaseProfile.Charge(SellPhase.Access, Ticks(1));
            SellPhaseProfile.Charge(SellPhase.Accept, Ticks(140));
            SellPhaseProfile.Charge(SellPhase.Remove, Ticks(60));
            SellPhaseProfile.Charge(SellPhase.Analytics, Ticks(9));
            SellPhaseProfile.Charge(SellPhase.Flush, Ticks(2));
            SellPhaseProfile.Charge(SellPhase.Purchase, Ticks(30));
            SellPhaseProfile.Charge(SellPhase.Deposit, Ticks(120));
            SellPhaseProfile.Charge(SellPhase.Panel, Ticks(8));

            Assert.AreEqual(500.0, SellPhaseProfile.HandlerMs, 0.05);
            Assert.AreEqual(390.0, PhaseSumMs(), 0.05);
            Assert.AreEqual(110.0, SellPhaseProfile.RemainderMs, 0.05, "the remainder must carry the difference, not be assumed zero");

            Assert.AreEqual(SellPhaseProfile.HandlerMs, PhaseSumMs() + SellPhaseProfile.RemainderMs, 0.01,
                "THE INVARIANT: phases plus the remainder are the handler total");
        }

        [TestMethod]
        public void AMissingPhaseShowsAsRemainderRatherThanAsAFastPhase()
        {
            // The failure this instrument was built to make impossible: a phase that was never wired up. Here
            // Payout is the un-wired one - it is never charged, and the cost it should have carried has to
            // appear in the remainder rather than vanishing or being spread over its neighbours.
            SellPhaseProfile.ChargeHandler(Ticks(200));
            SellPhaseProfile.Charge(SellPhase.Accept, Ticks(50));

            Assert.AreEqual(0, SellPhaseProfile.CallsAt(SellPhase.Payout), "the un-wired phase reports no calls at all");
            Assert.AreEqual(0.0, SellPhaseProfile.TotalMsAt(SellPhase.Payout), 0.001);
            Assert.AreEqual(150.0, SellPhaseProfile.RemainderMs, 0.05, "every unattributed millisecond has to be visible somewhere");
            Assert.AreEqual(SellPhaseProfile.HandlerMs, PhaseSumMs() + SellPhaseProfile.RemainderMs, 0.01);
        }

        [TestMethod]
        public void PerItemPhasesAccumulateIntoOneSlotWithACallCount()
        {
            // A 200-item sale must emit the same bounded set of fields as a 1-item sale.
            SellPhaseProfile.ChargeHandler(Ticks(400));

            for (var i = 0; i < 200; i++)
                SellPhaseProfile.Charge(SellPhase.Deposit, Ticks(1.5));

            Assert.AreEqual(200, SellPhaseProfile.CallsAt(SellPhase.Deposit));
            Assert.AreEqual(300.0, SellPhaseProfile.TotalMsAt(SellPhase.Deposit), 0.5);
            Assert.AreEqual(SellPhaseProfile.HandlerMs, PhaseSumMs() + SellPhaseProfile.RemainderMs, 0.01);
        }

        [TestMethod]
        public void TwoSalesInOneTickAggregateAndTheInvariantStillHolds()
        {
            SellPhaseProfile.ChargeHandler(Ticks(100));
            SellPhaseProfile.Charge(SellPhase.Accept, Ticks(40));

            SellPhaseProfile.ChargeHandler(Ticks(60));
            SellPhaseProfile.Charge(SellPhase.Accept, Ticks(20));

            Assert.AreEqual(2, SellPhaseProfile.HandlerCalls);
            Assert.AreEqual(160.0, SellPhaseProfile.HandlerMs, 0.05);
            Assert.AreEqual(2, SellPhaseProfile.CallsAt(SellPhase.Accept));
            Assert.AreEqual(SellPhaseProfile.HandlerMs, PhaseSumMs() + SellPhaseProfile.RemainderMs, 0.01);
        }

        [TestMethod]
        public void ABackwardsElapsedIsClampedRatherThanSubtracted()
        {
            SellPhaseProfile.Charge(SellPhase.Remove, Ticks(5));
            SellPhaseProfile.Charge(SellPhase.Remove, -Ticks(1000));

            Assert.AreEqual(2, SellPhaseProfile.CallsAt(SellPhase.Remove));
            Assert.AreEqual(5.0, SellPhaseProfile.TotalMsAt(SellPhase.Remove), 0.05, "a non-monotonic timestamp must not drive a total negative");
        }

        [TestMethod]
        public void BeginPhaseClearsTheAggregateEvenWhileDisabled()
        {
            SellPhaseProfile.ChargeHandler(Ticks(10));
            SellPhaseProfile.Charge(SellPhase.Panel, Ticks(4));

            Assert.AreEqual(1, SellPhaseProfile.HandlerCalls);

            // Seed the unmeasured-deposit counter too, so the zero asserted for it below is not vacuous: a
            // handler scope with a deposit that charged nothing is exactly what NoteDepositExecuted counts.
            var seed = SellPhaseProfile.BeginHandler();
            SellPhaseProfile.NoteDepositExecuted(SellPhaseProfile.DepositCalls);
            SellPhaseProfile.EndHandler(seed);

            Assert.AreEqual(1, SellPhaseProfile.DepositUnmeasured);

            InboundOpcodeProfile.Enabled = false;
            SellPhaseProfile.BeginPhase();

            Assert.AreEqual(0, SellPhaseProfile.HandlerCalls, "a tick that ran with the capture off must not leave stale numbers for the next emit");
            Assert.AreEqual(0.0, SellPhaseProfile.HandlerMs, 0.001);
            Assert.AreEqual(0.0, SellPhaseProfile.RemainderMs, 0.001);
            Assert.AreEqual(0, SellPhaseProfile.CallsAt(SellPhase.Panel));
            Assert.AreEqual(0.0, PhaseSumMs(), 0.001);
            Assert.AreEqual(0, SellPhaseProfile.DepositUnmeasured, "the unmeasured-deposit counter is per tick like everything else");
        }

        [TestMethod]
        public void BeginPhaseDisarmsAScopeLeftOpenByAnEscapingException()
        {
            // HandleActionSellItem closes its handler scope in a finally, so this is belt and braces - but the
            // aggregate must not be corruptible by a leaked scope even so. Without BeginPhase's `open = false`
            // a scope that escaped its finally would stay armed across the tick boundary, and the NEXT tick's
            // ApproachVendor or TryDeposit would be charged against a handler total this tick already
            // reported, driving sell_rest_ms negative on a line that names no sale at all.
            //
            // Ported from VaultDepositPhaseProfileTests, which had this coverage while the instrument it was
            // modelled on did not.
            var leaked = SellPhaseProfile.BeginHandler();

            Assert.AreNotEqual(0L, leaked, "the handler scope must open, or this test proves nothing");

            SellPhaseProfile.BeginPhase(); // stands in for the next tick starting

            var stray = SellPhaseProfile.Begin();

            Assert.AreEqual(0L, stray, "BeginPhase must disarm, or a leaked scope would keep charging into the next tick");

            Spin(1);
            SellPhaseProfile.End(SellPhase.Panel, stray);

            Assert.AreEqual(0, SellPhaseProfile.CallsAt(SellPhase.Panel));

            // control: a scope opened properly after the reset DOES charge, so the zero above pins the disarm
            // rather than an instrument BeginPhase broke outright
            var reopened = SellPhaseProfile.BeginHandler();

            Assert.AreNotEqual(0L, reopened, "control: BeginPhase must not have broken the instrument, only disarmed it");

            var live = SellPhaseProfile.Begin();
            SellPhaseProfile.End(SellPhase.Panel, live);
            SellPhaseProfile.EndHandler(reopened);

            Assert.AreEqual(1, SellPhaseProfile.CallsAt(SellPhase.Panel));
        }

        [TestMethod]
        public void EveryPhaseHasATagName()
        {
            Assert.AreEqual(SellPhases.Count, Enum.GetValues(typeof(SellPhase)).Length, "a phase without a tag name would render as an out-of-range crash on the emit path");
            Assert.AreEqual(SellPhases.Count, SellPhases.TagNames.Length);

            foreach (var name in SellPhases.TagNames)
            {
                Assert.IsFalse(string.IsNullOrEmpty(name));
                Assert.IsFalse(name.Contains(" ") || name.Contains("="), $"'{name}' would break the logfmt parse");
            }
        }

        // ---- wiring: the real scope API over real elapsed time ----

        [TestMethod]
        public void RealScopesSumToTheHandlerTotalPlusTheRemainder()
        {
            // The production call sequence in miniature: an enclosing validate region with access and per-item
            // accept inside it, a per-item remove/analytics loop, a flush, an enclosing purchase region with
            // per-item deposit and a trailing panel inside it, then payout. Plus unattributed work at the top,
            // which is what the remainder has to carry.
            var handler = SellPhaseProfile.BeginHandler();

            Assert.AreNotEqual(0L, handler, "the handler scope must open: enabled, on the world thread");

            Spin(2); // unattributed: the IsBusy guard, the landblock lookup, CalculatePayoutCoinAmount

            var validate = SellPhaseProfile.Begin();
            var validateCharged = SellPhaseProfile.ChargedTicks;

            var access = SellPhaseProfile.Begin();
            Spin(0.5);
            SellPhaseProfile.End(SellPhase.Access, access);

            for (var i = 0; i < 4; i++)
            {
                var accept = SellPhaseProfile.Begin();
                Spin(0.5);
                SellPhaseProfile.End(SellPhase.Accept, accept);
            }

            Spin(0.5);
            SellPhaseProfile.EndExclusive(SellPhase.Validate, validate, validateCharged);

            for (var i = 0; i < 4; i++)
            {
                var remove = SellPhaseProfile.Begin();
                Spin(0.5);
                SellPhaseProfile.End(SellPhase.Remove, remove);

                var analytics = SellPhaseProfile.Begin();
                Spin(0.25);
                SellPhaseProfile.End(SellPhase.Analytics, analytics);
            }

            var flush = SellPhaseProfile.Begin();
            Spin(0.5);
            SellPhaseProfile.End(SellPhase.Flush, flush);

            var purchase = SellPhaseProfile.Begin();
            var purchaseCharged = SellPhaseProfile.ChargedTicks;

            for (var i = 0; i < 4; i++)
            {
                var deposit = SellPhaseProfile.Begin();
                Spin(0.75);
                SellPhaseProfile.End(SellPhase.Deposit, deposit);
            }

            var panel = SellPhaseProfile.Begin();
            Spin(0.5);
            SellPhaseProfile.End(SellPhase.Panel, panel);

            SellPhaseProfile.EndExclusive(SellPhase.Purchase, purchase, purchaseCharged);

            var payout = SellPhaseProfile.Begin();
            Spin(0.5);
            SellPhaseProfile.End(SellPhase.Payout, payout);

            SellPhaseProfile.EndHandler(handler);

            Assert.AreEqual(1, SellPhaseProfile.HandlerCalls);

            Assert.AreEqual(SellPhaseProfile.HandlerMs, PhaseSumMs() + SellPhaseProfile.RemainderMs, 0.01,
                "THE INVARIANT, on measured time: the ten phases plus the remainder are the handler total");

            Assert.IsTrue(SellPhaseProfile.RemainderMs > 0.5,
                $"the 2 ms of work outside every phase must reach the remainder; it was {SellPhaseProfile.RemainderMs:0.###} ms");

            // Per-item phases carried their counts, so a reader can tell 4 cheap deposits from 1 expensive one.
            Assert.AreEqual(4, SellPhaseProfile.CallsAt(SellPhase.Deposit));
            Assert.AreEqual(4, SellPhaseProfile.CallsAt(SellPhase.Accept));
            Assert.AreEqual(1, SellPhaseProfile.CallsAt(SellPhase.Validate));
            Assert.AreEqual(1, SellPhaseProfile.CallsAt(SellPhase.Panel));

            TestContext.WriteLine(
                $"SELL_PHASE sum={PhaseSumMs():0.###} rest={SellPhaseProfile.RemainderMs:0.###} handler={SellPhaseProfile.HandlerMs:0.###}");
        }

        [TestMethod]
        public void AnEnclosingPhaseExcludesTheInnerPhasesItContains()
        {
            var handler = SellPhaseProfile.BeginHandler();

            var purchase = SellPhaseProfile.Begin();
            var purchaseCharged = SellPhaseProfile.ChargedTicks;

            Spin(1); // the enclosing region's own work

            var deposit = SellPhaseProfile.Begin();
            Spin(8); // the inner region, deliberately much larger
            SellPhaseProfile.End(SellPhase.Deposit, deposit);

            SellPhaseProfile.EndExclusive(SellPhase.Purchase, purchase, purchaseCharged);
            SellPhaseProfile.EndHandler(handler);

            var inner = SellPhaseProfile.TotalMsAt(SellPhase.Deposit);
            var outer = SellPhaseProfile.TotalMsAt(SellPhase.Purchase);

            Assert.IsTrue(inner > 6, $"the inner phase must have measured its own work; it was {inner:0.###} ms");
            Assert.IsTrue(outer < inner, $"the enclosing phase must not contain the inner one: outer {outer:0.###} ms, inner {inner:0.###} ms");

            // Control: the same nesting closed as a LEAF double-counts, which is what EndExclusive prevents -
            // so the assertion above pins the subtraction rather than a Spin that never ran.
            SellPhaseProfile.BeginPhase();

            var handler2 = SellPhaseProfile.BeginHandler();
            var purchase2 = SellPhaseProfile.Begin();

            Spin(1);

            var deposit2 = SellPhaseProfile.Begin();
            Spin(8);
            SellPhaseProfile.End(SellPhase.Deposit, deposit2);

            SellPhaseProfile.End(SellPhase.Purchase, purchase2); // leaf close: the bug being guarded against
            SellPhaseProfile.EndHandler(handler2);

            Assert.IsTrue(SellPhaseProfile.TotalMsAt(SellPhase.Purchase) > SellPhaseProfile.TotalMsAt(SellPhase.Deposit),
                "control: a leaf close DOES swallow the inner phase's time");
            Assert.IsTrue(SellPhaseProfile.RemainderMs < 0,
                "control: and the double count drives the remainder negative, which is why a negative is reported rather than clamped");
        }

        [TestMethod]
        public void WorkOutsideEveryPhaseLandsInTheRemainder()
        {
            // Two runs of the same total shape. The second moves one region out of its phase, and the ONLY
            // number that may change is the remainder - the phases must not silently absorb it.
            var handlerA = SellPhaseProfile.BeginHandler();
            var covered = SellPhaseProfile.Begin();
            Spin(6);
            SellPhaseProfile.End(SellPhase.Accept, covered);
            SellPhaseProfile.EndHandler(handlerA);

            var restWhenCovered = SellPhaseProfile.RemainderMs;
            var acceptWhenCovered = SellPhaseProfile.TotalMsAt(SellPhase.Accept);

            SellPhaseProfile.BeginPhase();

            var handlerB = SellPhaseProfile.BeginHandler();
            Spin(6); // the same work, now inside no phase at all
            SellPhaseProfile.EndHandler(handlerB);

            Assert.IsTrue(acceptWhenCovered > 4, $"the covered run must have charged the phase; it was {acceptWhenCovered:0.###} ms");
            Assert.IsTrue(restWhenCovered < 2, $"the covered run's remainder must be small; it was {restWhenCovered:0.###} ms");
            Assert.IsTrue(SellPhaseProfile.RemainderMs > 4, $"the uncovered run's cost must reach the remainder; it was {SellPhaseProfile.RemainderMs:0.###} ms");
            Assert.AreEqual(0.0, SellPhaseProfile.TotalMsAt(SellPhase.Accept), 0.001, "and no phase may have absorbed it");
            Assert.AreEqual(SellPhaseProfile.HandlerMs, PhaseSumMs() + SellPhaseProfile.RemainderMs, 0.01);
        }

        // ---- gating: off, and outside a handler scope, nothing may be charged ----

        [TestMethod]
        public void DisabledOpensNoScopeAndRecordsNothing()
        {
            InboundOpcodeProfile.Enabled = false;

            var handler = SellPhaseProfile.BeginHandler();

            Assert.AreEqual(0L, handler, "the handler scope must not open while the slow-tick log is off");

            var phase = SellPhaseProfile.Begin();

            Assert.AreEqual(0L, phase, "and no phase scope may open inside it either");

            Spin(2);

            SellPhaseProfile.End(SellPhase.Accept, phase);
            SellPhaseProfile.EndHandler(handler);

            Assert.AreEqual(0, SellPhaseProfile.HandlerCalls);
            Assert.AreEqual(0, SellPhaseProfile.CallsAt(SellPhase.Accept));
            Assert.AreEqual(0.0, SellPhaseProfile.HandlerMs, 0.001, "End of a 0 scope must be inert, not a charge of (now - 0)");

            // control: the identical sequence with the switch back on does record
            InboundOpcodeProfile.Enabled = true;

            var liveHandler = SellPhaseProfile.BeginHandler();
            var livePhase = SellPhaseProfile.Begin();

            Assert.AreNotEqual(0L, liveHandler);
            Assert.AreNotEqual(0L, livePhase);

            Spin(2);

            SellPhaseProfile.End(SellPhase.Accept, livePhase);
            SellPhaseProfile.EndHandler(liveHandler);

            Assert.AreEqual(1, SellPhaseProfile.HandlerCalls);
            Assert.AreEqual(1, SellPhaseProfile.CallsAt(SellPhase.Accept));
        }

        [TestMethod]
        public void APhaseOpenedOutsideAnyHandlerScopeIsNotCharged()
        {
            // ApproachVendor and TryDeposit are instrumented and are both reachable from paths that are not a
            // sale - the buy handler's panel refresh, a vault command. A charge from one of those would put
            // time in the aggregate that no handler total contains, so Begin has to refuse outside a handler.
            var strayBefore = SellPhaseProfile.Begin();

            Assert.AreEqual(0L, strayBefore, "no handler scope is open, so this must refuse");

            Spin(1);
            SellPhaseProfile.End(SellPhase.Panel, strayBefore);

            Assert.AreEqual(0, SellPhaseProfile.CallsAt(SellPhase.Panel));

            // And after a handler has closed, the instrument must be disarmed again.
            var handler = SellPhaseProfile.BeginHandler();

            var inside = SellPhaseProfile.Begin();
            Assert.AreNotEqual(0L, inside, "control: inside the handler scope the same call DOES open");
            SellPhaseProfile.End(SellPhase.Panel, inside);

            SellPhaseProfile.EndHandler(handler);

            var strayAfter = SellPhaseProfile.Begin();

            Assert.AreEqual(0L, strayAfter, "EndHandler must disarm, or a later buy-path ApproachVendor would be charged to the sale");
            Assert.AreEqual(1, SellPhaseProfile.CallsAt(SellPhase.Panel), "exactly the one charge from inside the scope");
            Assert.IsTrue(SellPhaseProfile.RemainderMs >= 0, "nothing charged from outside means the remainder cannot go negative");
        }

        /// <summary>
        /// Runs <paramref name="body"/> on a thread that has never called WorldTickProfile.BeginIteration, so
        /// WorldTickProfile.IsWorldThread is false there. An explicit Thread rather than Task.Run, so the work
        /// cannot land back on this thread under any scheduling. Same helper shape as
        /// InboundOpcodeProfileTests.OnAForeignThread.
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
            // The deposit scope sits inside a queued work item. It drains inline on the calling thread today,
            // but if it ever stopped doing so the measurement must be LOST rather than charged to whatever the
            // world thread happened to be doing.
            var handler = SellPhaseProfile.BeginHandler();

            var onThread = SellPhaseProfile.Begin();
            Assert.AreNotEqual(0L, onThread, "control: on the world thread, with the handler open, the scope DOES open");
            SellPhaseProfile.End(SellPhase.Deposit, onThread);

            OnAForeignThread(() =>
            {
                Assert.IsTrue(InboundOpcodeProfile.Enabled, "the capture must still be on, or this proves nothing");

                var foreign = SellPhaseProfile.Begin();

                Assert.AreEqual(0L, foreign, "a work item run off the world thread must open no scope");

                SellPhaseProfile.End(SellPhase.Deposit, foreign);
            });

            SellPhaseProfile.EndHandler(handler);

            Assert.AreEqual(1, SellPhaseProfile.CallsAt(SellPhase.Deposit), "only the world thread's charge may be in the aggregate");
        }

        // ---- the off-thread deposit: losing the measurement is acceptable, losing the FACT is not ----

        [TestMethod]
        public void ADepositThatChargedNothingIsCountedAsUnmeasured()
        {
            // Deterministic stand-in for the real race. The production sequence is: read DepositCalls on the
            // world thread, hand the closure to AccountVaultStore.Enqueue, then NoteDepositExecuted once it
            // returns. Here the closure is driven directly - once on the world thread (the normal case) and
            // once on a foreign thread (what a reaper-owned drain does to it) - so the two outcomes are
            // separated without depending on winning a lock race.
            var handler = SellPhaseProfile.BeginHandler();

            // CONTROL FIRST: charged on the world thread, so nothing is counted as unmeasured.
            var beforeCharged = SellPhaseProfile.DepositCalls;

            var scope = SellPhaseProfile.Begin();
            Spin(1);
            SellPhaseProfile.End(SellPhase.Deposit, scope);

            SellPhaseProfile.NoteDepositExecuted(beforeCharged);

            Assert.AreEqual(1, SellPhaseProfile.CallsAt(SellPhase.Deposit));
            Assert.AreEqual(0, SellPhaseProfile.DepositUnmeasured, "a deposit that DID charge must not be counted as unmeasured");

            var chargedMs = SellPhaseProfile.TotalMsAt(SellPhase.Deposit);

            // THE REAL CASE: the same closure body, executed where the profile cannot measure it.
            var beforeForeign = SellPhaseProfile.DepositCalls;

            OnAForeignThread(() =>
            {
                var foreign = SellPhaseProfile.Begin();

                Assert.AreEqual(0L, foreign, "the profile is world-thread-only, so the charge cannot happen here");

                Spin(1);

                SellPhaseProfile.End(SellPhase.Deposit, foreign);
            });

            SellPhaseProfile.NoteDepositExecuted(beforeForeign);

            SellPhaseProfile.EndHandler(handler);

            Assert.AreEqual(1, SellPhaseProfile.CallsAt(SellPhase.Deposit), "the foreign run added no charge");
            Assert.AreEqual(chargedMs, SellPhaseProfile.TotalMsAt(SellPhase.Deposit), 0.001, "and no time either");
            Assert.AreEqual(1, SellPhaseProfile.DepositUnmeasured, "so the FACT that it ran unmeasured has to be counted");

            // Two deposits ran; the line reports one charged plus one unmeasured, which is what lets a reader
            // tell "deposit was cheap" from "deposit was not measured".
            Assert.AreEqual(2, SellPhaseProfile.CallsAt(SellPhase.Deposit) + SellPhaseProfile.DepositUnmeasured);

            // And the counter perturbs nothing: no time was added or removed by any of the above.
            Assert.AreEqual(SellPhaseProfile.HandlerMs, PhaseSumMs() + SellPhaseProfile.RemainderMs, 0.01);
        }

        [TestMethod]
        public void NoteDepositExecutedIsInertOutsideAHandlerScopeAndWhileDisabled()
        {
            // Outside any handler scope - which is also every code path that is not a sale.
            SellPhaseProfile.NoteDepositExecuted(0);

            Assert.AreEqual(0, SellPhaseProfile.DepositUnmeasured);

            InboundOpcodeProfile.Enabled = false;

            var offHandler = SellPhaseProfile.BeginHandler();

            Assert.AreEqual(0L, offHandler);
            Assert.AreEqual(0, SellPhaseProfile.DepositCalls, "with the gate off the capture reads as empty rather than stale");

            SellPhaseProfile.NoteDepositExecuted(SellPhaseProfile.DepositCalls);
            SellPhaseProfile.EndHandler(offHandler);

            Assert.AreEqual(0, SellPhaseProfile.DepositUnmeasured, "nothing may be counted while the capture is off");

            // control: the identical sequence with the switch back on DOES count, so the zeros above pin the
            // guards rather than a counter that never fires
            InboundOpcodeProfile.Enabled = true;

            var onHandler = SellPhaseProfile.BeginHandler();
            SellPhaseProfile.NoteDepositExecuted(SellPhaseProfile.DepositCalls);
            SellPhaseProfile.EndHandler(onHandler);

            Assert.AreEqual(1, SellPhaseProfile.DepositUnmeasured);
        }

        [TestMethod]
        public void EveryDepositIsAccountedForWhileAnotherThreadContendsForTheSameStore()
        {
            // The real mechanism, against a real AccountVaultStore. AccountVaultBarrelReaper's sweep
            // (AccountVaultBarrelReaper.cs:216 starts its thread, :160 enqueues per row) hits the SAME
            // per-account store a sale is depositing into, and Drain runs every queued item on whichever
            // thread holds drainLock (AccountVaultStore.cs:2419-2484) - so a sale's deposit closure can
            // execute on the contender's thread.
            //
            // The race is a lock handoff and is not forced here, so the assertion is NOT "it happened": it is
            // that every deposit which ran is accounted for EXACTLY ONCE, as a charge or as an unmeasured
            // one, whichever thread won. That holds on every interleaving and would fail if
            // NoteDepositExecuted mis-attributed either outcome. How often the race actually fired is
            // reported rather than asserted, because asserting it would flake on a single-core runner.
            var backend = new FakeVaultBackend();
            var world = new FakeVaultWorld();
            var store = new AccountVaultStore(ContendedAccount, backend, world);

            var stop = false;

            // Stands in for the reaper: Enqueue against the same store, off the world thread, in a tight
            // loop, so it is frequently waiting on drainLock at the moment the sale releases it after its
            // own append - which is the window in which it picks up the sale's item.
            var contender = new Thread(() =>
            {
                while (!Volatile.Read(ref stop))
                    store.Enqueue(() => { });
            })
            {
                IsBackground = true,
                Name = "SellPhaseProfileTests contender",
            };

            contender.Start();

            var ran = 0;

            var handler = SellPhaseProfile.BeginHandler();

            try
            {
                for (var i = 0; i < 400; i++)
                {
                    // The production sequence, PersonalVendor.DepositItems in miniature.
                    var depositCallsBefore = SellPhaseProfile.DepositCalls;

                    var executed = false;

                    Action work = () =>
                    {
                        var depositScope = SellPhaseProfile.Begin();

                        executed = true;

                        SellPhaseProfile.End(SellPhase.Deposit, depositScope);
                    };

                    if (!store.Enqueue(work))
                        continue;

                    SellPhaseProfile.NoteDepositExecuted(depositCallsBefore);

                    Assert.IsTrue(executed, "Enqueue must not return before the queued work has run");

                    ran++;
                }
            }
            finally
            {
                SellPhaseProfile.EndHandler(handler);

                Volatile.Write(ref stop, true);

                Assert.IsTrue(contender.Join(TimeSpan.FromSeconds(30)), "the contending thread did not finish");
            }

            Assert.AreEqual(400, ran, "the fake store is never evicted, so no Enqueue may have been refused");

            var charged = SellPhaseProfile.CallsAt(SellPhase.Deposit);
            var unmeasured = SellPhaseProfile.DepositUnmeasured;

            Assert.AreEqual(ran, charged + unmeasured,
                $"every deposit that ran must be accounted for exactly once: {charged} charged + {unmeasured} unmeasured against {ran} run");

            Assert.AreEqual(SellPhaseProfile.HandlerMs, PhaseSumMs() + SellPhaseProfile.RemainderMs, 0.01,
                "and the contention must not have disturbed the sum invariant");

            TestContext.WriteLine($"SELL_PHASE_CONTENTION {ran} deposits: {charged} charged on the world thread, {unmeasured} run by the contender");
        }

        // ---- cost: nothing allocates, on or off ----

        /// <summary>
        /// One synthetic sale through the real scope API in the production order, with no work inside the
        /// scopes: what is being measured is the instrument, not the handler.
        /// </summary>
        private static void DriveOneSale(int items)
        {
            var handler = SellPhaseProfile.BeginHandler();

            var validate = SellPhaseProfile.Begin();
            var validateCharged = SellPhaseProfile.ChargedTicks;

            var access = SellPhaseProfile.Begin();
            SellPhaseProfile.End(SellPhase.Access, access);

            for (var i = 0; i < items; i++)
            {
                var accept = SellPhaseProfile.Begin();
                SellPhaseProfile.End(SellPhase.Accept, accept);
            }

            SellPhaseProfile.EndExclusive(SellPhase.Validate, validate, validateCharged);

            for (var i = 0; i < items; i++)
            {
                var remove = SellPhaseProfile.Begin();
                SellPhaseProfile.End(SellPhase.Remove, remove);

                var analytics = SellPhaseProfile.Begin();
                SellPhaseProfile.End(SellPhase.Analytics, analytics);
            }

            var flush = SellPhaseProfile.Begin();
            SellPhaseProfile.End(SellPhase.Flush, flush);

            var purchase = SellPhaseProfile.Begin();
            var purchaseCharged = SellPhaseProfile.ChargedTicks;

            for (var i = 0; i < items; i++)
            {
                var deposit = SellPhaseProfile.Begin();
                SellPhaseProfile.End(SellPhase.Deposit, deposit);
            }

            var panel = SellPhaseProfile.Begin();
            SellPhaseProfile.End(SellPhase.Panel, panel);

            SellPhaseProfile.EndExclusive(SellPhase.Purchase, purchase, purchaseCharged);

            var payout = SellPhaseProfile.Begin();
            SellPhaseProfile.End(SellPhase.Payout, payout);

            SellPhaseProfile.EndHandler(handler);
        }

        [TestMethod]
        public void SellPhaseCaptureAllocatesNothingWhileEnabled()
        {
            for (var i = 0; i < 200; i++) // warm-up: JIT and tiering
                DriveOneSale(76);

            SellPhaseProfile.BeginPhase(); // so the counts asserted below are the measured window's alone

            var before = GC.GetAllocatedBytesForCurrentThread();

            for (var i = 0; i < 1_000; i++)
                DriveOneSale(76);

            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.AreEqual(0L, allocated, $"the sell-phase capture allocated {allocated} bytes over 1,000 76-item sales");

            // control: the zero above was measured with the capture actually recording, not switched off
            Assert.AreEqual(1_000, SellPhaseProfile.HandlerCalls, "1,000 handler scopes must have been charged, so recording is what was measured");
            Assert.AreEqual(76_000, SellPhaseProfile.CallsAt(SellPhase.Deposit));
        }

        [TestMethod]
        public void TheDisabledPathAllocatesNothingAndDoesNoWork()
        {
            for (var i = 0; i < 200; i++) // warm-up with the capture ON, so the JIT sees the recording path too
                DriveOneSale(76);

            InboundOpcodeProfile.Enabled = false;
            SellPhaseProfile.BeginPhase();

            for (var i = 0; i < 200; i++)
                DriveOneSale(76);

            var before = GC.GetAllocatedBytesForCurrentThread();

            for (var i = 0; i < 1_000; i++)
                DriveOneSale(76);

            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.AreEqual(0L, allocated, $"the disabled sell-phase path allocated {allocated} bytes over 1,000 76-item sales");

            // The real claim about the disabled path is not "it allocates nothing" but "it does nothing":
            // BeginHandler returns 0 before touching any state, so no scope opens, no timestamp is taken and
            // no counter moves. That is observable, and it is the assertion that would fail if a future edit
            // moved work above the gate.
            Assert.AreEqual(0, SellPhaseProfile.HandlerCalls, "with the gate off not one of those 1,200 sales may have been recorded");
            Assert.AreEqual(0.0, SellPhaseProfile.HandlerMs, 0.001);
            Assert.AreEqual(0.0, PhaseSumMs(), 0.001, "and no phase may have been charged either");
            Assert.AreEqual(0.0, SellPhaseProfile.RemainderMs, 0.001);
        }

        [TestMethod]
        public void ReportSellPhaseCost()
        {
            const int items = 76;
            const int sales = 20_000;

            double MeasurePerSaleUs(bool enabled)
            {
                InboundOpcodeProfile.Enabled = enabled;

                for (var i = 0; i < 500; i++)
                    DriveOneSale(items);

                var sw = Stopwatch.StartNew();

                for (var i = 0; i < sales; i++)
                    DriveOneSale(items);

                sw.Stop();

                return sw.Elapsed.TotalMilliseconds * 1_000.0 / sales;
            }

            var on = MeasurePerSaleUs(true);
            var off = MeasurePerSaleUs(false);

            // Reported, not asserted: a wall-clock bound is flaky on a shared runner. The shape to sanity
            // check is that `off` is a rounding error against a real 527 ms sale and that `on` is too.
            TestContext.WriteLine(
                $"SELL_PHASE_COST {items}-item sale x {sales:N0}: on={on:0.###} us/sale, off={off:0.###} us/sale " +
                $"({(items * 4 + 7)} scope pairs per sale)");
        }

        // ---- rendering: the field names and the shape that ships ----

        private static string Render(SlowTickSnapshot s)
        {
            var sb = new StringBuilder();

            SlowTickLine.Append(sb, s);

            return sb.ToString();
        }

        private static SlowTickSnapshot SellSample()
        {
            // The prod sale this instrument was built for: one ga_Sell, 76 items, 527.62 ms. The per-phase
            // split below is illustrative, not measured; what the test pins is the field names, the order and
            // the arithmetic.
            var s = new SlowTickSnapshot();

            s.Iteration = 3092783;
            s.TotalMs = 536.61;
            s.WorldMs = 8.42;
            s.MedianMs = 11.75;
            s.Players = 41;
            s.ShardQueue = 0;
            s.WeenieMiss = 0;

            s.PhaseMs[(int)WorldTickPhase.InboundMessages] = 527.62;

            s.WorldUpdated = false;

            s.OfferOpcode(InboundOpcodeProfile.ActionKey((int)ACE.Server.Network.GameAction.GameActionType.Sell), 1, 527.62, 527.62);

            s.SellCalls = 1;
            s.SellMs = 527.11;

            s.SellPhaseCalls[(int)SellPhase.Validate] = 1;
            s.SellPhaseMs[(int)SellPhase.Validate] = 4.82;
            s.SellPhaseCalls[(int)SellPhase.Access] = 1;
            s.SellPhaseMs[(int)SellPhase.Access] = 0.03;
            s.SellPhaseCalls[(int)SellPhase.Accept] = 76;
            s.SellPhaseMs[(int)SellPhase.Accept] = 132.4;
            s.SellPhaseCalls[(int)SellPhase.Remove] = 76;
            s.SellPhaseMs[(int)SellPhase.Remove] = 61.55;
            s.SellPhaseCalls[(int)SellPhase.Analytics] = 76;
            s.SellPhaseMs[(int)SellPhase.Analytics] = 9.12;
            s.SellPhaseCalls[(int)SellPhase.Flush] = 1;
            s.SellPhaseMs[(int)SellPhase.Flush] = 0.44;
            s.SellPhaseCalls[(int)SellPhase.Purchase] = 1;
            s.SellPhaseMs[(int)SellPhase.Purchase] = 18.6;
            s.SellPhaseCalls[(int)SellPhase.Deposit] = 76;
            s.SellPhaseMs[(int)SellPhase.Deposit] = 271.29;
            s.SellPhaseCalls[(int)SellPhase.Payout] = 0;
            s.SellPhaseMs[(int)SellPhase.Payout] = 0;
            s.SellPhaseCalls[(int)SellPhase.Panel] = 1;
            s.SellPhaseMs[(int)SellPhase.Panel] = 23.77;

            var sum = 0.0;

            for (var i = 0; i < SellPhases.Count; i++)
                sum += s.SellPhaseMs[i];

            s.SellRestMs = s.SellMs - sum;

            return s;
        }

        [TestMethod]
        public void TheRenderedLineCarriesEveryPhaseAndTheRemainderInAFixedOrder()
        {
            var s = SellSample();

            var line = Render(s);

            Assert.IsFalse(line.Contains("\n"), "one line");

            Assert.IsTrue(line.EndsWith(
                    " op_calls=1 op_ms=527.62 op1=ga_Sell op1_n=1 op1_ms=527.62 op1_max_ms=527.62" +
                    " sell_n=1 sell_ms=527.11 sell_rest_ms=5.09 sell_deposit_unmeasured_n=0" +
                    " sell_validate_n=1 sell_validate_ms=4.82" +
                    " sell_access_n=1 sell_access_ms=0.03" +
                    " sell_accept_n=76 sell_accept_ms=132.4" +
                    " sell_remove_n=76 sell_remove_ms=61.55" +
                    " sell_analytics_n=76 sell_analytics_ms=9.12" +
                    " sell_flush_n=1 sell_flush_ms=0.44" +
                    " sell_purchase_n=1 sell_purchase_ms=18.6" +
                    " sell_deposit_n=76 sell_deposit_ms=271.29" +
                    " sell_payout_n=0 sell_payout_ms=0" +
                    " sell_panel_n=1 sell_panel_ms=23.77"),
                line);

            TestContext.WriteLine(line);
        }

        [TestMethod]
        public void TheRenderedPhasesPlusTheRemainderAddUpToTheRenderedTotal()
        {
            // Reads the numbers back OUT of the rendered line and checks the identity there, so the invariant
            // is pinned on what an operator actually parses rather than only on the in-memory fields. The
            // fields are rendered at two decimals, so the epsilon is the rounding of eleven of them.
            var line = Render(SellSample());

            double Field(string key)
            {
                var at = line.IndexOf(" " + key + "=", StringComparison.Ordinal);

                Assert.AreNotEqual(-1, at, $"{key} is missing from the line: {line}");

                var from = at + key.Length + 2;
                var to = line.IndexOf(' ', from);

                var raw = to < 0 ? line.Substring(from) : line.Substring(from, to - from);

                return double.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);
            }

            var sum = Field("sell_rest_ms");

            foreach (var tag in SellPhases.TagNames)
                sum += Field("sell_" + tag + "_ms");

            Assert.AreEqual(Field("sell_ms"), sum, 0.06,
                "THE INVARIANT as an operator reads it: the ten sell_<phase>_ms plus sell_rest_ms are sell_ms");

            Assert.IsTrue(Field("sell_ms") <= Field("op1_ms"),
                "sell_ms measures the handler; op1_ms additionally covers the payload parse and the dispatch around it");
        }

        [TestMethod]
        public void TheSellBlockIsOmittedEntirelyWhenNoSaleRan()
        {
            var s = SellSample();

            s.ResetSell();

            var line = Render(s);

            Assert.IsFalse(line.Contains("sell_"), $"an iteration with no sale in it must not carry the block at all: {line}");
            Assert.IsTrue(line.EndsWith(" op1_max_ms=527.62"), line);
        }
    }
}
