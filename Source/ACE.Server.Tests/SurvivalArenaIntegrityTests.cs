using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.WorldObjects;

using Reason = ACE.Server.WorldObjects.Player.SurvivalEjectReason;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Guards Player.ShouldEjectSurvivalRun, the arena-integrity decision the Proving Grounds (Defense) escalation
    /// tick runs before advancing a tier. An unusable arena must void the run (forfeit, no score) rather than let
    /// the survived-seconds clock run forever - the 2026-09-30 incident ran past tier 74 in an arena whose eleven
    /// server-side objects all existed, which is why the engagement-stall case is pinned with the full creature
    /// roster and exit portal present.
    /// </summary>
    [TestClass]
    public class SurvivalArenaIntegrityTests
    {
        // the shipped arena: four Squallbound Zefir + four Squallbound Wisp
        private const int ShippedRoster = 8;

        private const long DefaultStall = 60;

        // a populated arena well past the population grace - the steady state every tick after the first sees
        private static Reason Populated(int liveCreatureCount, bool hasExitPortal, double secondsSinceLastEngagement, long stallSeconds)
        {
            return Player.ShouldEjectSurvivalRun(liveCreatureCount, hasExitPortal, true, 600, secondsSinceLastEngagement, stallSeconds);
        }

        // ---- population grace: an instance still loading must not be voided for being empty YET ----

        [TestMethod]
        public void StillPopulating_InsideGrace_EmptyArenaIsNotEjected()
        {
            // first tick (10 s) of a slow load: no creatures and no portal have been built yet
            Assert.AreEqual(Reason.None, Player.ShouldEjectSurvivalRun(0, false, false, 10, 10, DefaultStall));
            Assert.AreEqual(Reason.None, Player.ShouldEjectSurvivalRun(0, false, false, 44.9, 44.9, DefaultStall));
        }

        [TestMethod]
        public void NeverPopulated_PastGrace_IsEjected()
        {
            Assert.AreEqual(Reason.NoCreatures, Player.ShouldEjectSurvivalRun(0, false, false, Player.SurvivalPopulationGraceSeconds, 45, DefaultStall),
                "a landblock that never completes must still eject once the run is 45 s old");
            Assert.AreEqual(Reason.NoExitPortal, Player.ShouldEjectSurvivalRun(ShippedRoster, false, false, 50, 5, DefaultStall));
        }

        [TestMethod]
        public void Populated_EarlyInRun_EmptyArenaIsEjected()
        {
            // the grace is for loading only: a COMPLETED load with nothing in it is broken from the first tick
            Assert.AreEqual(Reason.NoCreatures, Player.ShouldEjectSurvivalRun(0, true, true, 10, 10, DefaultStall));
            Assert.AreEqual(Reason.NoExitPortal, Player.ShouldEjectSurvivalRun(ShippedRoster, false, true, 10, 10, DefaultStall));
        }

        [TestMethod]
        public void StillPopulating_DoesNotSuspendTheStallTest()
        {
            Assert.AreEqual(Reason.EngagementStall, Player.ShouldEjectSurvivalRun(0, false, false, 30, 30, 20),
                "the population grace skips only the existence tests");
        }

        [TestMethod]
        public void HealthyArena_RecentlyEngaged_Continues()
        {
            Assert.AreEqual(Reason.None, Populated(ShippedRoster, true, 5, DefaultStall));
        }

        [TestMethod]
        public void NoLiveCreatures_Ejects()
        {
            Assert.AreEqual(Reason.NoCreatures, Populated(0, true, 5, DefaultStall));
        }

        [TestMethod]
        public void NoExitPortal_Ejects()
        {
            Assert.AreEqual(Reason.NoExitPortal, Populated(ShippedRoster, false, 5, DefaultStall));
        }

        [TestMethod]
        public void IncidentShape_EverythingPresentButNeverEngaged_Ejects()
        {
            // tier 74 at a 10 s interval: 740 s with the whole roster and the exit portal present and no attack
            Assert.AreEqual(Reason.EngagementStall, Populated(ShippedRoster, true, 740, DefaultStall));
        }

        [TestMethod]
        public void Stall_EjectsAtTheLimit()
        {
            Assert.AreEqual(Reason.EngagementStall, Populated(ShippedRoster, true, 60, DefaultStall));
        }

        [TestMethod]
        public void Stall_JustUnderTheLimit_Continues()
        {
            Assert.AreEqual(Reason.None, Populated(ShippedRoster, true, 59.9, DefaultStall));
        }

        [TestMethod]
        public void StallSecondsZero_DisablesOnlyTheStallTest()
        {
            Assert.AreEqual(Reason.None, Populated(ShippedRoster, true, 740, 0),
                "survival_stall_eject_seconds 0 must switch the stall test off");
            Assert.AreEqual(Reason.NoCreatures, Populated(0, true, 740, 0),
                "the off switch must not disable the existence tests");
        }

        [TestMethod]
        public void NegativeStallSeconds_IsTreatedAsDisabled()
        {
            Assert.AreEqual(Reason.None, Populated(ShippedRoster, true, 740, -1));
        }

        [TestMethod]
        public void SeveralFailures_ReportTheFirstInOrder()
        {
            Assert.AreEqual(Reason.NoCreatures, Populated(0, false, 740, DefaultStall));
            Assert.AreEqual(Reason.NoExitPortal, Populated(ShippedRoster, false, 740, DefaultStall));
        }
    }
}
