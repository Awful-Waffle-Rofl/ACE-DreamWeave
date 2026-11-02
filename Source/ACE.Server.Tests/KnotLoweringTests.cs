using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Knots;

namespace ACE.Server.Tests
{
    /// <summary>The /knot lower rule (pure).</summary>
    [TestClass]
    public class KnotLoweringTests
    {
        [TestMethod]
        public void FloorIs200()
        {
            Assert.AreEqual(200, KnotLowering.MinLevel);
        }

        [TestMethod]
        public void Lower_ToFloor_IsOk()
        {
            Assert.AreEqual(LowerVerdict.Ok, KnotLowering.Decide(342, 0, 200));
        }

        [TestMethod]
        public void Lower_OneAboveFloor_IsOk()
        {
            Assert.AreEqual(LowerVerdict.Ok, KnotLowering.Decide(342, null, 201));
        }

        [TestMethod]
        public void Lower_BelowFloor_IsRefused()
        {
            Assert.AreEqual(LowerVerdict.BelowFloor, KnotLowering.Decide(342, 0, 199));
            Assert.AreEqual(LowerVerdict.BelowFloor, KnotLowering.Decide(342, 0, -5));
        }

        [TestMethod]
        public void Lower_ToCurrent_IsRefused()
        {
            Assert.AreEqual(LowerVerdict.NotLower, KnotLowering.Decide(342, 0, 342));
        }

        [TestMethod]
        public void Lower_ToHigher_IsRefused()
        {
            Assert.AreEqual(LowerVerdict.NotLower, KnotLowering.Decide(342, 0, 343));
        }

        [TestMethod]
        public void Lower_KnotAtOrBelowFloor_IsRefused()
        {
            Assert.AreEqual(LowerVerdict.AlreadyAtFloor, KnotLowering.Decide(200, 0, 200));
            Assert.AreEqual(LowerVerdict.AlreadyAtFloor, KnotLowering.Decide(150, 0, 100));
        }

        [TestMethod]
        public void Lower_OneAboveFloorKnot_CanOnlyGoToFloor()
        {
            Assert.AreEqual(LowerVerdict.Ok, KnotLowering.Decide(201, 0, 200));
            Assert.AreEqual(LowerVerdict.NotLower, KnotLowering.Decide(201, 0, 201));
        }

        [TestMethod]
        public void Lower_WithRunCheckpoint_IsRefused()
        {
            Assert.AreEqual(LowerVerdict.RunInProgress, KnotLowering.Decide(342, 2, 300));
        }

        [TestMethod]
        public void Lower_LiveAttempt_IsRefusedEvenAtPhaseZero()
        {
            Assert.AreEqual(LowerVerdict.RunInProgress, KnotLowering.Decide(342, 0, 300, liveAttempt: true));
            Assert.AreEqual(LowerVerdict.Ok, KnotLowering.Decide(342, 0, 300, liveAttempt: false));
        }

        [TestMethod]
        public void NoLevelKnot_RefusalSaysNoLevel()
        {
            StringAssert.Contains(KnotLowering.Refusal(LowerVerdict.AlreadyAtFloor, 0), "no level");
        }

        [TestMethod]
        public void EveryRefusalHasAMessage_AndOkHasNone()
        {
            foreach (var v in new[] { LowerVerdict.AlreadyAtFloor, LowerVerdict.BelowFloor, LowerVerdict.NotLower, LowerVerdict.RunInProgress })
                Assert.IsFalse(string.IsNullOrEmpty(KnotLowering.Refusal(v, 342)), v.ToString());

            Assert.IsNull(KnotLowering.Refusal(LowerVerdict.Ok, 342));
        }

        [TestMethod]
        public void NameFor_UnboundAndBound()
        {
            Assert.AreEqual("Knot (Level 250)", KnotLowering.NameFor(null, 250));
            Assert.AreEqual(KnotItems.KnotName(250), KnotLowering.NameFor("", 250));
        }

        [TestMethod]
        public void NameFor_Bound_UsesBoundFormWithPinnacleNameOrKeyFallback()
        {
            // No pinnacle store is loaded in a unit test, so KnotClearRecords.KnotName falls back to the key itself.
            Assert.AreEqual(KnotItems.BoundKnotName(KnotClearRecords.KnotName("no_such_pinnacle"), 250), KnotLowering.NameFor("no_such_pinnacle", 250));
            Assert.AreEqual("Knot of the no_such_pinnacle (Level 250)", KnotLowering.NameFor("no_such_pinnacle", 250));
        }
    }
}
