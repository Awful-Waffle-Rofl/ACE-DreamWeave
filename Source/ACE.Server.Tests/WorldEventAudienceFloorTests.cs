using System.Collections.Generic;

using ACE.Entity.Enum;
using ACE.Server.WorldEvents;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The two pure halves of the scaling-audience rule: who is allowed into the sample
    /// (<see cref="WorldEventAudienceSampler.CountsTowardAudience"/>) and the one-participant floor the
    /// estimate is clamped up to (<see cref="WorldEventRosterSelector.WithFloor"/>). Neither touches a
    /// Player, a session or a landblock, so both are testable without a world (D6).
    /// </summary>
    [TestClass]
    public class WorldEventAudienceFloorTests
    {
        // ---- the floor -------------------------------------------------------------------------------

        [TestMethod]
        public void FloorEstimate_IsOneParticipantAtLevelFifty_WithAComputedPowerSum()
        {
            var floor = WorldEventRosterSelector.FloorEstimate;

            // The two dials, read through a variable so the assertion is a real comparison rather than a
            // constant the compiler folds away (MSTEST0032).
            var dials = (WorldEventRosterSelector.FloorParticipantCount, WorldEventRosterSelector.FloorParticipantLevel);

            Assert.AreEqual((1, 50), dials, "one participant at level 50");

            Assert.AreEqual(1, floor.Count);
            Assert.AreEqual(50, floor.MedianLevel);
            Assert.AreEqual(50, floor.P90Level);

            Assert.AreEqual(1, floor.Levels.Count, "the level list is what makes PowerSum non-zero");
            Assert.AreEqual(50, floor.Levels[0]);

            // (50 / 275)^2 = (2/11)^2 = 4/121.
            Assert.AreEqual(4d / 121d, floor.PowerSum, 0.0000001);
            Assert.AreEqual(0.0330578512396694, floor.PowerSum, 0.0000000001);
        }

        [TestMethod]
        public void WithFloor_LeavesAnyRealAudienceExactlyAsItWas()
        {
            var real = WorldEventRosterSelector.EstimateFromLevels(new List<int> { 30, 120, 200 });

            var floored = WorldEventRosterSelector.WithFloor(real);

            Assert.AreEqual(real.Count, floored.Count);
            Assert.AreEqual(real.MedianLevel, floored.MedianLevel);
            Assert.AreEqual(real.P90Level, floored.P90Level);
            Assert.AreEqual(real.PowerSum, floored.PowerSum, 0.0000001);
            Assert.AreSame(real.Levels, floored.Levels, "identity, not a rebuild");
        }

        [TestMethod]
        public void WithFloor_AtExactlyOneParticipant_IsStillIdentity()
        {
            // A single level-1 bystander is BELOW the floor's power but is not below its COUNT, and the rule
            // is count-only: a real sample is never rewritten, however weak it is.
            var one = WorldEventRosterSelector.EstimateFromLevels(new List<int> { 1 });

            var floored = WorldEventRosterSelector.WithFloor(one);

            Assert.AreEqual(1, floored.Count);
            Assert.AreEqual(1, floored.MedianLevel);
            Assert.AreEqual(one.PowerSum, floored.PowerSum, 0.0000001);
        }

        [TestMethod]
        public void WithFloor_AtZero_IsTheFloorEstimate()
        {
            var floored = WorldEventRosterSelector.WithFloor(new AudienceEstimate(0, 0, 0));

            Assert.AreEqual(WorldEventRosterSelector.FloorParticipantCount, floored.Count);
            Assert.AreEqual(WorldEventRosterSelector.FloorParticipantLevel, floored.MedianLevel);
            Assert.AreEqual(WorldEventRosterSelector.FloorParticipantLevel, floored.P90Level);
            Assert.AreEqual(4d / 121d, floored.PowerSum, 0.0000001);
        }

        [TestMethod]
        public void EstimateFromLevels_StillReturnsAGenuineZero_TheFloorIsNotAppliedHere()
        {
            // "/worldevent simulate" hands this an explicit level list, so an empty one must keep reading as
            // zero. The floor belongs to WorldEvent.SetAudience alone.
            var empty = WorldEventRosterSelector.EstimateFromLevels(new List<int>());

            Assert.AreEqual(0, empty.Count);
            Assert.AreEqual(0, empty.MedianLevel);
            Assert.AreEqual(0, empty.P90Level);
            Assert.AreEqual(0, empty.PowerSum, 0.0000001);
        }

        // ---- who counts ------------------------------------------------------------------------------

        [TestMethod]
        public void CountsTowardAudience_PlayersAndAdvocatesCount()
        {
            Assert.IsTrue(WorldEventAudienceSampler.CountsTowardAudience(AccessLevel.Player));
            Assert.IsTrue(WorldEventAudienceSampler.CountsTowardAudience(AccessLevel.Advocate),
                "an advocate is a player with a support flag, not staff");
        }

        [TestMethod]
        public void CountsTowardAudience_SentinelAndAboveAreExcluded()
        {
            Assert.IsFalse(WorldEventAudienceSampler.CountsTowardAudience(AccessLevel.Sentinel));
            Assert.IsFalse(WorldEventAudienceSampler.CountsTowardAudience(AccessLevel.Envoy));
            Assert.IsFalse(WorldEventAudienceSampler.CountsTowardAudience(AccessLevel.Developer));
            Assert.IsFalse(WorldEventAudienceSampler.CountsTowardAudience(AccessLevel.Admin));
        }

        [TestMethod]
        public void CountsTowardAudience_ANullLevelCountsAsAPlayer()
        {
            Assert.IsTrue(WorldEventAudienceSampler.CountsTowardAudience(null),
                "the exclusion is for KNOWN staff; an unknown level must never silently drop a character");
        }
    }
}
