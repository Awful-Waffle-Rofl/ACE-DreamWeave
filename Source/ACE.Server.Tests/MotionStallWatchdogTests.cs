using System;
using System.Collections.Generic;

using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Physics;
using ACE.Server.Physics.Animation;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Motion-stall watchdog: the stall predicate, the "turn waiting behind IsAnimating" signal, the queue clear
    /// helper shared with HandleExitWorld, the queue timestamps, the tunable seams and the log rate limit.
    /// A real Player cannot be driven through the use path in this harness (Player's static constructor touches the
    /// world database), so the Player hook is tested through the pure pieces it is assembled from.
    /// </summary>
    [TestClass]
    public class MotionStallWatchdogTests
    {
        private const double Threshold = 10.0;
        private const double Dwell = 5.0;
        private const double Now = 1000.0;

        /// <summary>A stalled FastTick player: animating for 60 s, a turn waiting, on the ground, idle otherwise.</summary>
        private static MotionStallWatchdog.Snapshot Stalled() => new MotionStallWatchdog.Snapshot
        {
            FastTick = true,
            IsAnimating = true,
            TurnBlocked = true,
            Contact = true,
            Teleporting = false,
            Casting = false,
            AnimatingSince = Now - 60.0,
            TurnBlockedSeconds = 60.0,
        };

        // ---- the stall predicate ----

        [TestMethod]
        public void StaleAndWaiting_Clears()
        {
            Assert.IsTrue(MotionStallWatchdog.ShouldAutoClear(true, Stalled(), Now, Threshold, Dwell));
        }

        [TestMethod]
        public void StaleButNothingWaiting_Untouched()
        {
            var s = Stalled();
            s.TurnBlocked = false;
            Assert.IsFalse(MotionStallWatchdog.ShouldAutoClear(true, s, Now, Threshold, Dwell));
        }

        [TestMethod]
        public void FreshAndWaiting_Untouched()
        {
            var s = Stalled();
            s.AnimatingSince = Now - (Threshold - 0.5);
            Assert.IsFalse(MotionStallWatchdog.ShouldAutoClear(true, s, Now, Threshold, Dwell));

            // exactly at the threshold is still not stale (strictly greater)
            s.AnimatingSince = Now - Threshold;
            Assert.IsFalse(MotionStallWatchdog.ShouldAutoClear(true, s, Now, Threshold, Dwell));
        }

        [TestMethod]
        public void SwitchOff_Untouched()
        {
            Assert.IsFalse(MotionStallWatchdog.ShouldAutoClear(false, Stalled(), Now, Threshold, Dwell));
        }

        [TestMethod]
        public void NotAnimating_Untouched()
        {
            var s = Stalled();
            s.IsAnimating = false;
            Assert.IsFalse(MotionStallWatchdog.ShouldAutoClear(true, s, Now, Threshold, Dwell));
        }

        [DataTestMethod]
        [DataRow("notFastTick")]
        [DataRow("airborne")]
        [DataRow("teleporting")]
        [DataRow("casting")]
        public void ExcludedStates_Untouched(string which)
        {
            var s = Stalled();
            switch (which)
            {
                case "notFastTick": s.FastTick = false; break;
                case "airborne": s.Contact = false; break;
                case "teleporting": s.Teleporting = true; break;
                case "casting": s.Casting = true; break;
            }

            // control: the unmodified snapshot clears, so the flip alone is what stops it
            Assert.IsTrue(MotionStallWatchdog.ShouldAutoClear(true, Stalled(), Now, Threshold, Dwell));
            Assert.IsFalse(MotionStallWatchdog.ShouldAutoClear(true, s, Now, Threshold, Dwell), which);
        }

        // ---- the "turn waiting behind IsAnimating" signal ----

        private static MoveToManager WaitingTurn()
        {
            var mtm = new MoveToManager();
            mtm.MovementType = MovementType.TurnToObject;
            mtm.PendingActions.Add(new MovementNode(MovementType.TurnToHeading, 90.0f));
            mtm.CurrentCommand = 0;
            return mtm;
        }

        [TestMethod]
        public void TurnBlocked_WhenTurnHeadNotYetTurning()
        {
            Assert.IsTrue(MotionStallWatchdog.IsTurnBlockedOnAnimation(WaitingTurn()));
        }

        [TestMethod]
        public void TurnBlocked_FalseForNullOrIdleManager()
        {
            Assert.IsFalse(MotionStallWatchdog.IsTurnBlockedOnAnimation(null));
            Assert.IsFalse(MotionStallWatchdog.IsTurnBlockedOnAnimation(new MoveToManager()));

            var mtm = WaitingTurn();
            mtm.MovementType = MovementType.Invalid;
            Assert.IsFalse(MotionStallWatchdog.IsTurnBlockedOnAnimation(mtm));
        }

        [DataTestMethod]
        [DataRow(MotionCommand.TurnRight)]
        [DataRow(MotionCommand.TurnLeft)]
        public void TurnBlocked_FalseWhileTurnIsRunning(MotionCommand turning)
        {
            var mtm = WaitingTurn();
            mtm.CurrentCommand = (uint)turning;
            Assert.IsFalse(MotionStallWatchdog.IsTurnBlockedOnAnimation(mtm));
        }

        [TestMethod]
        public void TurnBlocked_FalseWhenHeadIsMoveForward()
        {
            var mtm = WaitingTurn();
            mtm.PendingActions.Clear();
            mtm.PendingActions.Add(new MovementNode(MovementType.MoveToPosition));
            Assert.IsFalse(MotionStallWatchdog.IsTurnBlockedOnAnimation(mtm));
        }

        [TestMethod]
        public void TurnBlocked_FalseWithNoPendingNodes()
        {
            var mtm = WaitingTurn();
            mtm.PendingActions.Clear();
            Assert.IsFalse(MotionStallWatchdog.IsTurnBlockedOnAnimation(mtm));
        }

        [TestMethod]
        public void TurnBlocked_FalseWithAlwaysTurn()
        {
            var mtm = WaitingTurn();
            mtm.AlwaysTurn = true;
            Assert.IsFalse(MotionStallWatchdog.IsTurnBlockedOnAnimation(mtm));
        }

        // ---- the clear helper ----

        private static MotionInterp StuckInterp(params uint[] motions)
        {
            var minterp = new MotionInterp();
            minterp.PhysicsObj = new PhysicsObj();
            minterp.RawState = new RawMotionState();
            minterp.InterpretedState = new InterpretedMotionState();
            minterp.PendingMotions = new LinkedList<MotionNode>();

            foreach (var m in motions)
                minterp.PendingMotions.AddLast(new MotionNode(7, m, WeenieError.None));

            minterp.PhysicsObj.IsAnimating = minterp.PendingMotions.Count > 0;
            return minterp;
        }

        [TestMethod]
        public void Clear_ActionNode_RemovesMatchingActions_AndResetsQueue()
        {
            var minterp = StuckInterp((uint)MotionCommand.Ready, (uint)MotionCommand.Wave);
            minterp.InterpretedState.AddAction((uint)MotionCommand.Wave, 1.0f, 1, false);
            minterp.RawState.AddAction((uint)MotionCommand.Wave, 1.0f, 1, false);

            var cleared = minterp.ClearPendingMotions();

            Assert.AreEqual(2, cleared);
            Assert.AreEqual(0, minterp.PendingMotions.Count);
            Assert.IsFalse(minterp.PhysicsObj.IsAnimating);
            Assert.AreEqual(0, minterp.InterpretedState.Actions.Count, "Action-masked node must remove its interpreted action");
            Assert.AreEqual(0, minterp.RawState.Actions.Count, "Action-masked node must remove its raw action");
        }

        [TestMethod]
        public void Clear_NonActionNode_LeavesActionsAlone()
        {
            var minterp = StuckInterp((uint)MotionCommand.Ready, (uint)MotionCommand.TurnRight);
            minterp.InterpretedState.AddAction((uint)MotionCommand.Wave, 1.0f, 1, false);
            minterp.RawState.AddAction((uint)MotionCommand.Wave, 1.0f, 1, false);

            Assert.AreEqual(2, minterp.ClearPendingMotions());
            Assert.AreEqual(1, minterp.InterpretedState.Actions.Count);
            Assert.AreEqual(1, minterp.RawState.Actions.Count);
            Assert.IsFalse(minterp.PhysicsObj.IsAnimating);
        }

        [TestMethod]
        public void HandleExitWorld_StillClearsTheSameWay()
        {
            var minterp = StuckInterp((uint)MotionCommand.Wave);
            minterp.InterpretedState.AddAction((uint)MotionCommand.Wave, 1.0f, 1, false);
            minterp.RawState.AddAction((uint)MotionCommand.Wave, 1.0f, 1, false);

            minterp.HandleExitWorld();

            Assert.AreEqual(0, minterp.PendingMotions.Count);
            Assert.IsFalse(minterp.PhysicsObj.IsAnimating);
            Assert.AreEqual(0, minterp.InterpretedState.Actions.Count);
            Assert.AreEqual(0, minterp.RawState.Actions.Count);
        }

        // ---- queue timestamps ----

        [TestMethod]
        public void Timestamps_NonEmptySinceOnlyMovesWhenTheQueueStartsOrIsCleared()
        {
            var savedTicks = Timers.PortalYearTicks;
            try
            {
                var minterp = StuckInterp();

                Timers.PortalYearTicks = 100.0;
                minterp.add_to_queue(0, (uint)MotionCommand.Ready, WeenieError.None);
                Assert.AreEqual(100.0, minterp.PendingMotionsNonEmptySince);
                Assert.AreEqual(100.0, minterp.PendingMotionsChangedTime);

                // a second add to a non-empty queue (what every click's StopCompletely does) refreshes only the change time
                Timers.PortalYearTicks = 150.0;
                minterp.add_to_queue(0, (uint)MotionCommand.Ready, WeenieError.None);
                Assert.AreEqual(100.0, minterp.PendingMotionsNonEmptySince);
                Assert.AreEqual(150.0, minterp.PendingMotionsChangedTime);

                // a MotionDone that leaves the queue non-empty does not restart the animating clock
                Timers.PortalYearTicks = 160.0;
                minterp.MotionDone(true);
                Assert.AreEqual(1, minterp.PendingMotions.Count);
                Assert.AreEqual(100.0, minterp.PendingMotionsNonEmptySince);
                Assert.AreEqual(160.0, minterp.PendingMotionsChangedTime);

                // drained, then a fresh add restarts it
                minterp.MotionDone(true);
                Assert.IsFalse(minterp.PhysicsObj.IsAnimating);
                Timers.PortalYearTicks = 200.0;
                minterp.add_to_queue(0, (uint)MotionCommand.Ready, WeenieError.None);
                Assert.AreEqual(200.0, minterp.PendingMotionsNonEmptySince);

                // clear restarts it too
                Timers.PortalYearTicks = 250.0;
                minterp.ClearPendingMotions();
                Assert.AreEqual(250.0, minterp.PendingMotionsNonEmptySince);
                Assert.AreEqual(250.0, minterp.PendingMotionsChangedTime);
            }
            finally
            {
                Timers.PortalYearTicks = savedTicks;
            }
        }

        // ---- tunables ----

        [TestMethod]
        public void DefaultSeams_WithoutShardConfig_ReturnCompiledDefaults()
        {
            Assert.AreEqual(MotionStallWatchdog.DefaultEnabled, MotionStallWatchdog.Enabled());
            Assert.AreEqual(MotionStallWatchdog.DefaultThresholdSeconds, MotionStallWatchdog.ThresholdSeconds());
            Assert.IsTrue(MotionStallWatchdog.DefaultEnabled, "owner ruling: new settings default ON");
        }

        [TestMethod]
        public void Seams_AreSwappableAndRestorable()
        {
            var enabled = MotionStallWatchdog.Enabled;
            var threshold = MotionStallWatchdog.ThresholdSeconds;
            try
            {
                MotionStallWatchdog.Enabled = () => false;
                MotionStallWatchdog.ThresholdSeconds = () => 5.0;
                Assert.IsFalse(MotionStallWatchdog.Enabled());
                Assert.AreEqual(5.0, MotionStallWatchdog.ThresholdSeconds());
            }
            finally
            {
                MotionStallWatchdog.Enabled = enabled;
                MotionStallWatchdog.ThresholdSeconds = threshold;
            }

            Assert.IsTrue(MotionStallWatchdog.Enabled());
        }

        [TestMethod]
        public void ClampThreshold_FloorsAndRejectsNonFinite()
        {
            Assert.AreEqual(MotionStallWatchdog.MinThresholdSeconds, MotionStallWatchdog.ClampThreshold(0.0));
            Assert.AreEqual(MotionStallWatchdog.MinThresholdSeconds, MotionStallWatchdog.ClampThreshold(-10.0));
            Assert.AreEqual(MotionStallWatchdog.DefaultThresholdSeconds, MotionStallWatchdog.ClampThreshold(double.NaN));
            Assert.AreEqual(MotionStallWatchdog.DefaultThresholdSeconds, MotionStallWatchdog.ClampThreshold(double.PositiveInfinity));
            Assert.AreEqual(45.0, MotionStallWatchdog.ClampThreshold(45.0));
        }

        [TestMethod]
        public void ClampTurnBlocked_FloorsAndRejectsNonFinite()
        {
            Assert.AreEqual(MotionStallWatchdog.MinTurnBlockedSeconds, MotionStallWatchdog.ClampTurnBlocked(0.0));
            Assert.AreEqual(MotionStallWatchdog.MinTurnBlockedSeconds, MotionStallWatchdog.ClampTurnBlocked(-3.0));
            Assert.AreEqual(MotionStallWatchdog.DefaultTurnBlockedSeconds, MotionStallWatchdog.ClampTurnBlocked(double.NaN));
            Assert.AreEqual(MotionStallWatchdog.DefaultTurnBlockedSeconds, MotionStallWatchdog.ClampTurnBlocked(double.NegativeInfinity));
            Assert.AreEqual(3.0, MotionStallWatchdog.ClampTurnBlocked(3.0));
        }

        [TestMethod]
        public void DefaultTurnBlockedSeam_WithoutShardConfig_ReturnsCompiledDefault()
        {
            Assert.AreEqual(MotionStallWatchdog.DefaultTurnBlockedSeconds, MotionStallWatchdog.TurnBlockedSeconds());
        }

        [TestMethod]
        public void Defaults_AreTheOwnerRuledValues()
        {
            // owner ruling on #1502: queue clock 10 s, blocked-turn dwell 5 s
            Assert.AreEqual(10.0, MotionStallWatchdog.DefaultThresholdSeconds);
            Assert.AreEqual(5.0, MotionStallWatchdog.DefaultTurnBlockedSeconds);
        }

        // ---- the blocked-turn dwell ----

        [TestMethod]
        public void BlockedShorterThanDwell_Untouched()
        {
            var s = Stalled();
            s.TurnBlockedSeconds = Dwell - 0.1;
            Assert.IsFalse(MotionStallWatchdog.ShouldAutoClear(true, s, Now, Threshold, Dwell));

            // a normal click: blocked for a tick or two while its Ready node drains
            s.TurnBlockedSeconds = 0.05;
            Assert.IsFalse(MotionStallWatchdog.ShouldAutoClear(true, s, Now, Threshold, Dwell));
        }

        [TestMethod]
        public void BlockedAtOrPastDwell_Clears()
        {
            var s = Stalled();
            s.TurnBlockedSeconds = Dwell;
            Assert.IsTrue(MotionStallWatchdog.ShouldAutoClear(true, s, Now, Threshold, Dwell));

            s.TurnBlockedSeconds = Dwell + 5.0;
            Assert.IsTrue(MotionStallWatchdog.ShouldAutoClear(true, s, Now, Threshold, Dwell));
        }

        [TestMethod]
        public void NextTurnBlockedSince_StartsKeepsAndResets()
        {
            var notBlocked = MotionStallWatchdog.NotBlocked;

            // first blocked check starts the clock
            Assert.AreEqual(100.0, MotionStallWatchdog.NextTurnBlockedSince(true, notBlocked, 100.0));
            // later blocked checks keep the original start
            Assert.AreEqual(100.0, MotionStallWatchdog.NextTurnBlockedSince(true, 100.0, 105.0));
            // any unblocked check resets it
            Assert.AreEqual(notBlocked, MotionStallWatchdog.NextTurnBlockedSince(false, 100.0, 106.0));
            // and the next block starts fresh
            Assert.AreEqual(107.0, MotionStallWatchdog.NextTurnBlockedSince(true, notBlocked, 107.0));
        }

        // ---- the Player -> Snapshot mapping ----

        /// <summary>The primitives of a stalled FastTick player, as Player.CheckMotionStall would pass them.</summary>
        private static MotionStallWatchdog.Snapshot Build(
            bool fastTick = true, bool isAnimating = true, bool turnBlocked = true, double turnBlockedSince = Now - 60.0,
            TransientStateFlags transientState = TransientStateFlags.Contact | TransientStateFlags.OnWalkable,
            bool teleporting = false, bool casting = false, double animatingSince = Now - 60.0)
        {
            return MotionStallWatchdog.BuildSnapshot(fastTick, isAnimating, turnBlocked, turnBlockedSince, transientState, teleporting, casting, animatingSince, Now);
        }

        [TestMethod]
        public void BuildSnapshot_StalledPrimitives_Clear()
        {
            var s = Build();
            Assert.IsTrue(s.Contact);
            Assert.AreEqual(60.0, s.TurnBlockedSeconds, 1e-9);
            Assert.IsTrue(MotionStallWatchdog.ShouldAutoClear(true, s, Now, Threshold, Dwell));
        }

        [DataTestMethod]
        [DataRow("fastTick")]
        [DataRow("isAnimating")]
        [DataRow("turnBlocked")]
        [DataRow("turnBlockedSince")]
        [DataRow("contact")]
        [DataRow("teleporting")]
        [DataRow("casting")]
        [DataRow("animatingSince")]
        public void BuildSnapshot_EachFieldReachesThePredicate(string field)
        {
            MotionStallWatchdog.Snapshot s;
            switch (field)
            {
                case "fastTick": s = Build(fastTick: false); break;
                case "isAnimating": s = Build(isAnimating: false); break;
                case "turnBlocked": s = Build(turnBlocked: false); break;
                case "turnBlockedSince": s = Build(turnBlockedSince: Now - 1.0); break;
                case "contact": s = Build(transientState: TransientStateFlags.OnWalkable); break;
                case "teleporting": s = Build(teleporting: true); break;
                case "casting": s = Build(casting: true); break;
                case "animatingSince": s = Build(animatingSince: Now - 5.0); break;
                default: throw new ArgumentException(field);
            }

            Assert.IsFalse(MotionStallWatchdog.ShouldAutoClear(true, s, Now, Threshold, Dwell), field);
        }

        [TestMethod]
        public void BuildSnapshot_TurnBlockedMapsOnItsOwn()
        {
            // turnBlocked=false also zeroes the dwell, which hides a lost TurnBlocked mapping from the predicate
            // alone; pin the field itself, with a live dwell clock so only the flag differs
            var s = Build(turnBlocked: false, turnBlockedSince: Now - 60.0);
            Assert.IsFalse(s.TurnBlocked);
            Assert.IsTrue(Build().TurnBlocked);
        }

        [TestMethod]
        public void BuildSnapshot_NotBlockedSentinel_GivesZeroDwell()
        {
            var s = Build(turnBlockedSince: MotionStallWatchdog.NotBlocked);
            Assert.AreEqual(0.0, s.TurnBlockedSeconds);
            Assert.IsFalse(MotionStallWatchdog.ShouldAutoClear(true, s, Now, Threshold, Dwell));
        }

        // ---- empty-queue clear ----

        [TestMethod]
        public void NothingToClear_OnlyWhenEmptyAndNotAnimating()
        {
            Assert.IsTrue(MotionStallWatchdog.NothingToClear(0, false));
            Assert.IsFalse(MotionStallWatchdog.NothingToClear(0, true), "an orphaned IsAnimating is still worth clearing");
            Assert.IsFalse(MotionStallWatchdog.NothingToClear(3, true));
        }

        [TestMethod]
        public void StaleSeconds_IsZeroForAnEmptyQueue()
        {
            // an empty queue's non-empty timestamp is from its last run; it must not print as "stale for <huge>s"
            Assert.AreEqual(0.0, MotionStallWatchdog.StaleSeconds(0, 5000.0, 10.0));
            Assert.AreEqual(40.0, MotionStallWatchdog.StaleSeconds(2, 50.0, 10.0));
        }

        // ---- /motionstate name parsing ----

        private static readonly Dictionary<string, (string, bool)> Roster = new Dictionary<string, (string, bool)>(StringComparer.OrdinalIgnoreCase)
        {
            ["Quark"] = ("Quark", true),
            ["Mr Clear"] = ("Mr Clear", true),
            ["Mr"] = ("Mr", true),
        };

        private static (string, bool) Lookup(string n) => Roster.TryGetValue(n, out var hit) ? hit : (null, false);

        [TestMethod]
        public void TryResolve_PlainName()
        {
            Assert.IsTrue(ACE.Server.Command.Handlers.MotionStateCommands.TryResolve(new[] { "Quark" }, Lookup, out var found, out var online, out var clear, out _));
            Assert.AreEqual("Quark", found);
            Assert.IsTrue(online);
            Assert.IsFalse(clear);
        }

        [TestMethod]
        public void TryResolve_TrailingClearFlag()
        {
            Assert.IsTrue(ACE.Server.Command.Handlers.MotionStateCommands.TryResolve(new[] { "Quark", "clear" }, Lookup, out var found, out _, out var clear, out var name));
            Assert.AreEqual("Quark", found);
            Assert.AreEqual("Quark", name);
            Assert.IsTrue(clear);
        }

        [TestMethod]
        public void TryResolve_NameEndingInClear_IsTheName()
        {
            // "Mr" also exists, so reading the last word as the flag would pick the wrong character
            Assert.IsTrue(ACE.Server.Command.Handlers.MotionStateCommands.TryResolve(new[] { "Mr", "Clear" }, Lookup, out var found, out _, out var clear, out _));
            Assert.AreEqual("Mr Clear", found);
            Assert.IsFalse(clear);

            // and "Mr Clear clear" clears Mr Clear
            Assert.IsTrue(ACE.Server.Command.Handlers.MotionStateCommands.TryResolve(new[] { "Mr", "Clear", "clear" }, Lookup, out found, out _, out clear, out _));
            Assert.AreEqual("Mr Clear", found);
            Assert.IsTrue(clear);
        }

        [TestMethod]
        public void TryResolve_UnknownAndEmpty()
        {
            Assert.IsFalse(ACE.Server.Command.Handlers.MotionStateCommands.TryResolve(new[] { "Nobody", "clear" }, Lookup, out var found, out _, out _, out var name));
            Assert.IsNull(found);
            Assert.AreEqual("Nobody clear", name);

            Assert.IsFalse(ACE.Server.Command.Handlers.MotionStateCommands.TryResolve(new string[0], Lookup, out _, out _, out _, out name));
            Assert.AreEqual(string.Empty, name);

            // a lone "clear" is a name, never a flag with no name
            Assert.IsFalse(ACE.Server.Command.Handlers.MotionStateCommands.TryResolve(new[] { "clear" }, Lookup, out _, out _, out var clear, out _));
            Assert.IsFalse(clear);
        }

        // ---- logging ----

        [TestMethod]
        public void LogGate_PassesOncePerInterval_AndCountsSuppressed()
        {
            var gate = new MotionStallWatchdog.LogGate();
            var t0 = new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);

            Assert.IsTrue(gate.TryPass(t0, out var s0));
            Assert.AreEqual(0, s0);

            Assert.IsFalse(gate.TryPass(t0.AddSeconds(10), out _));
            Assert.IsFalse(gate.TryPass(t0.AddSeconds(20), out _));
            Assert.AreEqual(2, gate.Suppressed);

            Assert.IsTrue(gate.TryPass(t0 + MotionStallWatchdog.LogGate.Interval, out var s1));
            Assert.AreEqual(2, s1);
            Assert.AreEqual(0, gate.Suppressed);
        }

        [TestMethod]
        public void FormatClearLine_MatchesTheSpecifiedShape()
        {
            var motions = MotionStallWatchdog.DescribeMotions(new[]
            {
                new MotionNode(0, (uint)MotionCommand.Ready, WeenieError.None),
                new MotionNode(12, (uint)MotionCommand.Wave, WeenieError.None),
            });

            Assert.AreEqual("Ready/0, Wave/12", motions);

            var line = MotionStallWatchdog.FormatClearLine("Quark", 0x50000001, 2, 75.25, motions, true, "tick", 0);
            Assert.AreEqual("[MOTION_STALL] Quark (0x50000001) cleared 2 pending motion(s) stale for 75.3s: Ready/0, Wave/12 contact=True trigger=tick", line);

            var withSuppressed = MotionStallWatchdog.FormatClearLine("Quark", 0x50000001, 1, 31.0, "Ready/0", false, "supersede", 3);
            StringAssert.EndsWith(withSuppressed, "trigger=supersede suppressedSinceLast=3");
        }

        [TestMethod]
        public void DescribeMotions_CapsTheList()
        {
            var nodes = new List<MotionNode>();
            for (var i = 0; i < MotionStallWatchdog.MaxListedMotions + 5; i++)
                nodes.Add(new MotionNode(i, (uint)MotionCommand.Ready, WeenieError.None));

            StringAssert.EndsWith(MotionStallWatchdog.DescribeMotions(nodes), ", +5 more");
            Assert.AreEqual("(none)", MotionStallWatchdog.DescribeMotions(new List<MotionNode>()));
        }
    }
}
