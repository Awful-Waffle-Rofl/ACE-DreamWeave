using System.Collections.Generic;

using ACE.Entity.Enum;
using ACE.Server.Physics;
using ACE.Server.Physics.Animation;
using ACE.Server.Physics.Managers;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The watchdog clear must drain the MotionTableManager twin queue as well as MotionInterp's. A stall is a twin
    /// head whose NumAnims exceeds AnimationCounter with no link animation left to play; clearing only MotionInterp
    /// leaves that node in place, so every later motion queues behind it again.
    /// </summary>
    [TestClass]
    public class MotionStallTwinDrainTests
    {
        private static (PhysicsObj obj, MotionInterp minterp, MotionTableManager mtm) StuckPair()
        {
            // no weenie: the default WeenieObject has no WorldObject, and its OnMotionDone dereferences one
            var obj = new PhysicsObj { WeenieObj = null };

            var minterp = new MotionInterp();
            minterp.PhysicsObj = obj;
            minterp.RawState = new RawMotionState();
            minterp.InterpretedState = new InterpretedMotionState();
            minterp.PendingMotions = new LinkedList<MotionNode>();
            minterp.PendingMotions.AddLast(new MotionNode(7, (uint)MotionCommand.Ready, WeenieError.None));
            minterp.PendingMotions.AddLast(new MotionNode(8, (uint)MotionCommand.WalkForward, WeenieError.None));

            obj.MovementManager = new MovementManager { MotionInterpreter = minterp };

            // the stuck shape: head waits for 2 AnimDone hooks, has seen 1, and nothing is left to play
            var mtm = new MotionTableManager { PhysicsObj = obj, AnimationCounter = 1 };
            mtm.PendingAnimations.AddLast(new AnimNode((uint)MotionCommand.Ready, 2));
            mtm.PendingAnimations.AddLast(new AnimNode((uint)MotionCommand.WalkForward, 1));

            obj.PartArray = new PartArray { Owner = obj, MotionTableManager = mtm };
            obj.IsAnimating = true;

            return (obj, minterp, mtm);
        }

        [TestMethod]
        public void Drain_WithTwin_EmptiesBothQueues()
        {
            var (obj, minterp, mtm) = StuckPair();

            var cleared = MotionStallWatchdog.DrainPendingMotions(obj, minterp, drainTwin: true);

            Assert.AreEqual(2, cleared);
            Assert.AreEqual(0, mtm.PendingAnimations.Count, "the stuck twin node must be drained");
            Assert.AreEqual(0u, mtm.AnimationCounter);
            Assert.AreEqual(0, minterp.PendingMotions.Count);
            Assert.IsFalse(obj.IsAnimating);
        }

        [TestMethod]
        public void Drain_WithoutTwin_LeavesTheStuckTwinNode()
        {
            // the cast path (and the pre-fix clear): MotionInterp only. This is the re-stall mechanism.
            var (obj, minterp, mtm) = StuckPair();

            var cleared = MotionStallWatchdog.DrainPendingMotions(obj, minterp, drainTwin: false);

            Assert.AreEqual(2, cleared);
            Assert.AreEqual(0, minterp.PendingMotions.Count);
            Assert.AreEqual(2, mtm.PendingAnimations.Count);
            Assert.AreEqual(2u, mtm.PendingAnimations.First.Value.NumAnims);
        }

        [TestMethod]
        [DataRow(false, false, "tick", true)]
        [DataRow(false, false, "supersede", true)]
        [DataRow(false, false, "admin", true)]
        [DataRow(false, false, "cast_timeout", true)]
        [DataRow(false, true, "tick", true)]
        [DataRow(false, true, "cast_timeout", true)]
        [DataRow(true, false, "tick", false)]
        [DataRow(true, false, "supersede", false)]
        [DataRow(true, false, "admin", false)]
        [DataRow(true, false, "cast_timeout", false)]   // cast_timeout Release: MotionInterp only
        [DataRow(true, true, "cast_timeout", true)]     // cast_timeout Fail: FailCast follows in the same call
        [DataRow(true, true, "tick", false)]            // the allowance is bound to the cast_timeout trigger
        [DataRow(true, true, "supersede", false)]
        [DataRow(true, true, "admin", false)]
        public void ShouldDrainTwin_TruthTable(bool casting, bool allowWhileCasting, string trigger, bool expected)
        {
            Assert.AreEqual(expected, MotionStallWatchdog.ShouldDrainTwin(casting, allowWhileCasting, trigger));
        }

        [TestMethod]
        public void DescribeTwinQueue_ShowsHeadCountAndCounter()
        {
            var (_, _, mtm) = StuckPair();

            Assert.AreEqual("twinHead=Ready:2 twinCount=2 animCounter=1", MotionStallWatchdog.DescribeTwinQueue(mtm));
            Assert.IsNull(MotionStallWatchdog.DescribeTwinQueue(null));

            mtm.PendingAnimations.Clear();
            mtm.AnimationCounter = 0;
            Assert.AreEqual("twinHead=- twinCount=0 animCounter=0", MotionStallWatchdog.DescribeTwinQueue(mtm));
        }

        [TestMethod]
        public void FormatClearLine_AppendsTwinAfterTrigger()
        {
            var line = MotionStallWatchdog.FormatClearLine("Quark", 0x50000001, 1, 31.0, "Ready/0", false, "tick", 3, "twinHead=Ready:2 twinCount=2 animCounter=1");

            Assert.AreEqual("[MOTION_STALL] Quark (0x50000001) cleared 1 pending motion(s) stale for 31.0s: Ready/0 contact=False trigger=tick twinHead=Ready:2 twinCount=2 animCounter=1 suppressedSinceLast=3", line);
        }
    }
}
