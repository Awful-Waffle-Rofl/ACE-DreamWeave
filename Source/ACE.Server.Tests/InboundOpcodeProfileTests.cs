using System;
using System.Diagnostics;
using System.Threading;

using ACE.Server.Managers;
using ACE.Server.Network.GameAction;
using ACE.Server.Network.GameMessages;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// WaffleACE slow-tick capture: the per-opcode attribution of the inbound-message phase.
    ///
    /// Two halves. The WIRING tests drive the real scope API (BeginMessage / RelabelAsGameAction / EndInvoke)
    /// and assert what got charged and under which key - the two-layer dispatch question, where the whole point
    /// is that a game action is counted ONCE, under its GameActionType and not under the generic GameAction
    /// message opcode. The AGGREGATION tests drive Charge with synthetic tick amounts, because real elapsed
    /// time cannot be asserted to the millisecond without a flaky sleep; that is the same seam
    /// TickPhaseAccumulator gets from taking its timestamps as parameters.
    ///
    /// A third group pins the WORLD-THREAD GUARD on both entry points that write shared state. The contract the
    /// class states is that work reaching it from anywhere but the world thread is not measured rather than
    /// mislabelled, and the re-label entry point is the one where breaking that is silent: it would move the
    /// label of a charge the world thread is about to close, onto a plausible-looking opcode. Each of those
    /// tests carries its on-world-thread control, so a pass means the thread check fired and not that the
    /// operation never works.
    ///
    /// InboundOpcodeProfile is static (it sits on the world loop's hot path and must not be resolved through an
    /// interface), so every test starts from BeginPhase, and TestInitialize claims the world-thread role by
    /// calling WorldTickProfile.BeginIteration - the production gate is exercised as shipped rather than seamed
    /// around.
    /// </summary>
    [TestClass]
    public class InboundOpcodeProfileTests
    {
        private static readonly double TicksPerMs = Stopwatch.Frequency / 1000.0;

        [TestInitialize]
        public void Setup()
        {
            WorldTickProfile.BeginIteration(); // claims this thread as the world thread
            WorldTickProfile.EndIteration();

            InboundOpcodeProfile.Enabled = true;
            InboundOpcodeProfile.BeginPhase();
        }

        [TestCleanup]
        public void Cleanup()
        {
            InboundOpcodeProfile.Enabled = false;
            InboundOpcodeProfile.BeginPhase();
        }

        private static int IndexOfKey(long key)
        {
            for (var i = 0; i < InboundOpcodeProfile.FilledCount; i++)
            {
                if (InboundOpcodeProfile.KeyAt(i) == key)
                    return i;
            }

            return -1;
        }

        private static long Ticks(double ms) => (long)(ms * TicksPerMs);

        // ---- wiring: what a real scope charges, and under which key ----

        [TestMethod]
        public void APlainMessageIsChargedToItsMessageOpcode()
        {
            var scope = InboundOpcodeProfile.BeginMessage((int)GameMessageOpcode.CharacterEnterWorld);
            Assert.AreNotEqual(0L, scope, "the scope must open: enabled, on the world thread");
            InboundOpcodeProfile.EndInvoke(scope);

            Assert.AreEqual(1, InboundOpcodeProfile.FilledCount);
            Assert.AreEqual(InboundOpcodeProfile.MessageKey((int)GameMessageOpcode.CharacterEnterWorld), InboundOpcodeProfile.KeyAt(0));
            Assert.AreEqual(1, InboundOpcodeProfile.CallsAt(0));
            Assert.AreEqual(0, InboundOpcodeProfile.OverflowCalls);
        }

        [TestMethod]
        public void AGameActionIsChargedOnceToItsActionTypeNotToTheGenericMessageOpcode()
        {
            // the production sequence: HandleClientMessage opens the scope on GameMessageOpcode.GameAction,
            // then InboundMessageManager.HandleGameAction re-labels it from inside
            var scope = InboundOpcodeProfile.BeginMessage((int)GameMessageOpcode.GameAction);
            InboundOpcodeProfile.RelabelAsGameAction((int)GameActionType.PutItemInContainer);
            InboundOpcodeProfile.EndInvoke(scope);

            Assert.AreEqual(1, InboundOpcodeProfile.FilledCount, "one charge, not two: the outer layer must not be counted as well");
            Assert.AreEqual(InboundOpcodeProfile.ActionKey((int)GameActionType.PutItemInContainer), InboundOpcodeProfile.KeyAt(0));
            Assert.AreEqual(1, InboundOpcodeProfile.CallsAt(0));

            Assert.AreEqual(-1, IndexOfKey(InboundOpcodeProfile.MessageKey((int)GameMessageOpcode.GameAction)),
                "the generic GameAction opcode must not appear at all");
        }

        [TestMethod]
        public void MessageAndActionKeysOfTheSameNumericValueDoNotCollide()
        {
            // GameMessageOpcode and GameActionType are separate sparse enums with overlapping numeric ranges,
            // so the key space has to tell them apart. 0x0005 is SetSingleCharacterOption as an action.
            const int raw = 0x0005;

            InboundOpcodeProfile.Charge(InboundOpcodeProfile.MessageKey(raw), Ticks(1));
            InboundOpcodeProfile.Charge(InboundOpcodeProfile.ActionKey(raw), Ticks(2));

            Assert.AreEqual(2, InboundOpcodeProfile.FilledCount);
            Assert.AreNotEqual(InboundOpcodeProfile.MessageKey(raw), InboundOpcodeProfile.ActionKey(raw));
        }

        [TestMethod]
        public void ANewScopeStartsFromTheMessageLabelAgainAfterAnActionRelabel()
        {
            var a = InboundOpcodeProfile.BeginMessage((int)GameMessageOpcode.GameAction);
            InboundOpcodeProfile.RelabelAsGameAction((int)GameActionType.Talk);
            InboundOpcodeProfile.EndInvoke(a);

            var b = InboundOpcodeProfile.BeginMessage((int)GameMessageOpcode.CharacterEnterWorld);
            InboundOpcodeProfile.EndInvoke(b);

            Assert.AreEqual(2, InboundOpcodeProfile.FilledCount);
            Assert.AreNotEqual(-1, IndexOfKey(InboundOpcodeProfile.ActionKey((int)GameActionType.Talk)));
            Assert.AreNotEqual(-1, IndexOfKey(InboundOpcodeProfile.MessageKey((int)GameMessageOpcode.CharacterEnterWorld)),
                "a stale label from the previous scope must not leak into the next one");
        }

        [TestMethod]
        public void DisabledRecordsNothingAndEndInvokeIsANoOp()
        {
            InboundOpcodeProfile.Enabled = false;

            var scope = InboundOpcodeProfile.BeginMessage((int)GameMessageOpcode.GameAction);

            Assert.AreEqual(0L, scope, "the scope must not open while the slow-tick log is off");

            InboundOpcodeProfile.RelabelAsGameAction((int)GameActionType.Talk);
            InboundOpcodeProfile.EndInvoke(scope);

            Assert.AreEqual(0, InboundOpcodeProfile.FilledCount);

            // control: the same sequence with the switch back on does record, so the assertion above is not vacuous
            InboundOpcodeProfile.Enabled = true;

            var live = InboundOpcodeProfile.BeginMessage((int)GameMessageOpcode.GameAction);
            InboundOpcodeProfile.RelabelAsGameAction((int)GameActionType.Talk);
            InboundOpcodeProfile.EndInvoke(live);

            Assert.AreEqual(1, InboundOpcodeProfile.FilledCount);
        }

        [TestMethod]
        public void BeginPhaseClearsTheAggregateEvenWhileDisabled()
        {
            InboundOpcodeProfile.Charge(InboundOpcodeProfile.MessageKey(0x1234), Ticks(5));
            Assert.AreEqual(1, InboundOpcodeProfile.FilledCount);

            InboundOpcodeProfile.Enabled = false;
            InboundOpcodeProfile.BeginPhase();

            Assert.AreEqual(0, InboundOpcodeProfile.FilledCount, "a tick that runs with the capture off must not leave stale slots for the next emit");
            Assert.AreEqual(0, InboundOpcodeProfile.OverflowCalls);
            Assert.AreEqual(0.0, InboundOpcodeProfile.OverflowMs);
        }

        // ---- the world-thread guard: off the world thread the capture must LOSE the measurement, never corrupt it ----

        /// <summary>
        /// Runs <paramref name="body"/> on a thread that has never called WorldTickProfile.BeginIteration, so
        /// WorldTickProfile.IsWorldThread is false there. An explicit Thread rather than Task.Run, so the work
        /// cannot land back on this thread under any scheduling.
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
        public void BeginMessageOffTheWorldThreadOpensNoScopeAndRecordsNothing()
        {
            // control first: on the world thread, with the same settings, the scope DOES open - so a zero below
            // means the thread check fired and not that the capture was simply off
            var onThread = InboundOpcodeProfile.BeginMessage((int)GameMessageOpcode.CharacterEnterWorld);
            Assert.AreNotEqual(0L, onThread);
            InboundOpcodeProfile.EndInvoke(onThread);

            var filledBefore = InboundOpcodeProfile.FilledCount;

            OnAForeignThread(() =>
            {
                Assert.IsTrue(InboundOpcodeProfile.Enabled, "the capture must still be on, or this proves nothing");

                var scope = InboundOpcodeProfile.BeginMessage((int)GameMessageOpcode.GameAction);

                Assert.AreEqual(0L, scope, "a queued action run off the world thread must open no scope");

                InboundOpcodeProfile.EndInvoke(scope); // must be inert, not a charge of (now - 0)
            });

            Assert.AreEqual(filledBefore, InboundOpcodeProfile.FilledCount, "nothing may have been recorded from the foreign thread");
            Assert.AreEqual(0, InboundOpcodeProfile.OverflowCalls);
        }

        [TestMethod]
        public void RelabelOffTheWorldThreadDoesNotMoveTheLabelOfAnOpenScope()
        {
            // The label field is shared, so an off-thread re-label would silently re-attribute whatever charge
            // the world thread closes next - and to a plausible-looking opcode, which is worse than losing it.
            // Driven through the scope the world thread owns, because that is where the corruption would show.
            var scope = InboundOpcodeProfile.BeginMessage((int)GameMessageOpcode.GameAction);

            OnAForeignThread(() => InboundOpcodeProfile.RelabelAsGameAction((int)GameActionType.PutItemInContainer));

            InboundOpcodeProfile.EndInvoke(scope);

            Assert.AreEqual(1, InboundOpcodeProfile.FilledCount);
            Assert.AreEqual(InboundOpcodeProfile.MessageKey((int)GameMessageOpcode.GameAction), InboundOpcodeProfile.KeyAt(0),
                "the charge must keep the label the world thread gave it");
            Assert.AreEqual(-1, IndexOfKey(InboundOpcodeProfile.ActionKey((int)GameActionType.PutItemInContainer)),
                "the foreign thread's label must not appear anywhere in the aggregate");

            // control: the identical re-label ON the world thread does move the label, so the assertions above
            // pin the thread check rather than a re-label that never works
            InboundOpcodeProfile.BeginPhase();

            var onThread = InboundOpcodeProfile.BeginMessage((int)GameMessageOpcode.GameAction);
            InboundOpcodeProfile.RelabelAsGameAction((int)GameActionType.PutItemInContainer);
            InboundOpcodeProfile.EndInvoke(onThread);

            Assert.AreEqual(InboundOpcodeProfile.ActionKey((int)GameActionType.PutItemInContainer), InboundOpcodeProfile.KeyAt(0));
        }

        // ---- aggregation: counts, totals, max, overflow ----

        [TestMethod]
        public void RepeatedCallsOfOneOpcodeAccumulateCountTotalAndMax()
        {
            var key = InboundOpcodeProfile.ActionKey((int)GameActionType.TargetedMeleeAttack);

            InboundOpcodeProfile.Charge(key, Ticks(2));
            InboundOpcodeProfile.Charge(key, Ticks(9));
            InboundOpcodeProfile.Charge(key, Ticks(4));

            Assert.AreEqual(1, InboundOpcodeProfile.FilledCount, "one slot per distinct opcode");
            Assert.AreEqual(3, InboundOpcodeProfile.CallsAt(0));
            Assert.AreEqual(15.0, InboundOpcodeProfile.TotalMsAt(0), 0.01);
            Assert.AreEqual(9.0, InboundOpcodeProfile.MaxMsAt(0), 0.01, "max is the slowest single call, which is what separates one expensive call from many cheap ones");
        }

        [TestMethod]
        public void OneExpensiveCallAndManyCheapCallsAreDistinguishable()
        {
            var expensive = InboundOpcodeProfile.ActionKey((int)GameActionType.PutItemInContainer);
            var cheap = InboundOpcodeProfile.ActionKey((int)GameActionType.Talk);

            InboundOpcodeProfile.Charge(expensive, Ticks(500));

            for (var i = 0; i < 500; i++)
                InboundOpcodeProfile.Charge(cheap, Ticks(1));

            var e = IndexOfKey(expensive);
            var c = IndexOfKey(cheap);

            // same total time, opposite shapes
            Assert.AreEqual(InboundOpcodeProfile.TotalMsAt(e), InboundOpcodeProfile.TotalMsAt(c), 1.0);
            Assert.AreEqual(1, InboundOpcodeProfile.CallsAt(e));
            Assert.AreEqual(500, InboundOpcodeProfile.CallsAt(c));
            Assert.IsTrue(InboundOpcodeProfile.MaxMsAt(e) > 100 * InboundOpcodeProfile.MaxMsAt(c),
                "the slowest single call must separate them even though the totals match");
        }

        [TestMethod]
        public void OpcodesPastCapacityFallIntoTheOverflowBucketAndTheTotalsStayExact()
        {
            for (var i = 0; i < InboundOpcodeProfile.Capacity; i++)
                InboundOpcodeProfile.Charge(InboundOpcodeProfile.MessageKey(0x1000 + i), Ticks(1));

            Assert.AreEqual(InboundOpcodeProfile.Capacity, InboundOpcodeProfile.FilledCount);
            Assert.AreEqual(0, InboundOpcodeProfile.OverflowCalls);

            // three more distinct opcodes, plus a repeat of one that already has a slot
            InboundOpcodeProfile.Charge(InboundOpcodeProfile.MessageKey(0x2001), Ticks(3));
            InboundOpcodeProfile.Charge(InboundOpcodeProfile.MessageKey(0x2002), Ticks(4));
            InboundOpcodeProfile.Charge(InboundOpcodeProfile.MessageKey(0x2003), Ticks(5));
            InboundOpcodeProfile.Charge(InboundOpcodeProfile.MessageKey(0x1000), Ticks(7));

            Assert.AreEqual(InboundOpcodeProfile.Capacity, InboundOpcodeProfile.FilledCount, "capacity is a hard cap");
            Assert.AreEqual(3, InboundOpcodeProfile.OverflowCalls);
            Assert.AreEqual(12.0, InboundOpcodeProfile.OverflowMs, 0.05);

            Assert.AreEqual(2, InboundOpcodeProfile.CallsAt(0), "an opcode that already holds a slot keeps using it once the aggregator is full");
            Assert.AreEqual(8.0, InboundOpcodeProfile.TotalMsAt(0), 0.05);
        }

        [TestMethod]
        public void ABackwardsElapsedIsClampedRatherThanSubtracted()
        {
            var key = InboundOpcodeProfile.MessageKey(0x30);

            InboundOpcodeProfile.Charge(key, Ticks(5));
            InboundOpcodeProfile.Charge(key, -Ticks(1000));

            Assert.AreEqual(2, InboundOpcodeProfile.CallsAt(0));
            Assert.AreEqual(5.0, InboundOpcodeProfile.TotalMsAt(0), 0.01, "a non-monotonic timestamp must not drive a total negative");
        }
    }
}
