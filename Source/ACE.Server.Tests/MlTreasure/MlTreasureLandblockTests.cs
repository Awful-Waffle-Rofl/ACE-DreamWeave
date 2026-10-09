using ACE.Entity;
using ACE.Server.MlTreasure;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.MlTreasure
{
    /// <summary>
    /// The ML gate predicate, TREASURE-HUNT-PLAN.md section 4: realm 1 AND landblock x in
    /// 0x0C-0x2E AND y in 0xAC-0xCA. Pure, allocation-free - see MlTreasureLandblock.cs.
    /// </summary>
    [TestClass]
    public class MlTreasureLandblockTests
    {
        /// <summary>Bluespire (0x21B0), one of the five town anchors named in TREASURE-HUNT-PLAN.md
        /// section 4, sits inside the box: x=0x21, y=0xB0.</summary>
        [TestMethod]
        public void Bluespire_Realm1_IsMaraeLassel()
        {
            Assert.IsTrue(MlTreasureLandblock.IsMaraeLassel(0x21B0, realm: 1));
        }

        /// <summary>Same landblock, realm 0 (retail) - the realm check is not optional
        /// (TREASURE-HUNT-PLAN.md section 4).</summary>
        [TestMethod]
        public void Bluespire_Realm0_IsNotMaraeLassel()
        {
            Assert.IsFalse(MlTreasureLandblock.IsMaraeLassel(0x21B0, realm: 0));
        }

        /// <summary>A realm-1 landblock outside the box entirely - realm 1 also carries non-ML
        /// landblocks (TREASURE-HUNT-PLAN.md section 4), so realm alone must not be sufficient.
        /// 0x0007 (Town Network) is far outside x 0x0C-0x2E.</summary>
        [TestMethod]
        public void NonMlLandblock_Realm1_IsNotMaraeLassel()
        {
            Assert.IsFalse(MlTreasureLandblock.IsMaraeLassel(0x0007, realm: 1));
        }

        [TestMethod]
        public void BoxCorner_MinXMinY_IsMaraeLassel()
        {
            Assert.IsTrue(MlTreasureLandblock.IsMaraeLassel(0x0CAC, realm: 1));
        }

        [TestMethod]
        public void BoxCorner_MinXMaxY_IsMaraeLassel()
        {
            Assert.IsTrue(MlTreasureLandblock.IsMaraeLassel(0x0CCA, realm: 1));
        }

        [TestMethod]
        public void BoxCorner_MaxXMinY_IsMaraeLassel()
        {
            Assert.IsTrue(MlTreasureLandblock.IsMaraeLassel(0x2EAC, realm: 1));
        }

        [TestMethod]
        public void BoxCorner_MaxXMaxY_IsMaraeLassel()
        {
            Assert.IsTrue(MlTreasureLandblock.IsMaraeLassel(0x2ECA, realm: 1));
        }

        /// <summary>One step outside each edge of the box is rejected - confirms the comparisons are
        /// inclusive at the edges and not off-by-one.</summary>
        [TestMethod]
        public void OneStepOutsideEachEdge_IsNotMaraeLassel()
        {
            Assert.IsFalse(MlTreasureLandblock.IsMaraeLassel(0x0BAC, realm: 1), "x just below MinLbX");
            Assert.IsFalse(MlTreasureLandblock.IsMaraeLassel(0x2FAC, realm: 1), "x just above MaxLbX");
            Assert.IsFalse(MlTreasureLandblock.IsMaraeLassel(0x0CAB, realm: 1), "y just below MinLbY");
            Assert.IsFalse(MlTreasureLandblock.IsMaraeLassel(0x0CCB, realm: 1), "y just above MaxLbY");
        }

        // ---- Position pipeline pin (code review 2026-09-10, finding 1) -------------------------------
        //
        // TreasureMapHandler.TryHandleUse reads player.Location.LandblockId.Landblock and
        // player.Location.RealmID (the KillFillVessel.Accepts precedent, Source/ACE.Server/Entity/
        // KillFillVessel.cs) and feeds them straight into MlTreasureLandblock.IsMaraeLassel. A live
        // Player cannot be constructed in this test project (its ctor reaches DatabaseManager.
        // Authentication), but Position has no such dependency, so these tests exercise the exact same
        // accessor pipeline the handler uses - same landblock, only the realm differs - which is what
        // would have caught the gate being unwired: the plain MlTreasureLandblock tests above never
        // touch a Position at all, so they could not have shown TreasureMapHandler failing to call in.

        /// <summary>Bluespire (0x21B0) outdoor cell 1, realm 1 - the same landblock+realm pipeline
        /// TreasureMapHandler reads off player.Location.</summary>
        [TestMethod]
        public void PositionPipeline_BluespireOutdoors_Realm1_IsMaraeLassel()
        {
            var instance = Position.InstanceIDFromVars(realmId: 1, shortInstanceId: 0, isTemporaryRuleset: false);
            var pos = new Position(0x21B00001, 10f, 10f, 0f, 0f, 0f, 0f, 1f, instance);

            Assert.IsTrue(MlTreasureLandblock.IsMaraeLassel(pos.LandblockId.Landblock, pos.RealmID));
        }

        /// <summary>Same landblock and cell, realm 0 (retail) - must refuse. This is the exact case
        /// code review found unwired: retail Marae Lassel exists in realm 0, and GetMapCoords() is
        /// realm-agnostic, so without this gate a realm-0 player at the matching spot would pass.</summary>
        [TestMethod]
        public void PositionPipeline_BluespireOutdoors_Realm0_IsNotMaraeLassel()
        {
            var instance = Position.InstanceIDFromVars(realmId: 0, shortInstanceId: 0, isTemporaryRuleset: false);
            var pos = new Position(0x21B00001, 10f, 10f, 0f, 0f, 0f, 0f, 1f, instance);

            Assert.IsFalse(MlTreasureLandblock.IsMaraeLassel(pos.LandblockId.Landblock, pos.RealmID));
        }
    }
}
