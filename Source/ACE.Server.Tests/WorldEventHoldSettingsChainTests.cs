using System.Linq;

using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>The chain group of the Hold settings.</summary>
    public partial class WorldEventHoldSettingsTests
    {
        private static WorldEventRequest Stage2(GoalDef goal) =>
            WorldEventChain.NextRequest(new WorldEventRequest(), goal.Chain, 2, 2, 1, 1, null, "Rithwic");

        [TestMethod]
        public void Chain_WithNothingSet_IsExactlyAsBefore()
        {
            var goal = EnvoysGoal();

            using var scope = new HoldSettingsScope();

            Assert.AreEqual(2, goal.Chain.MaxLength);
            Assert.AreEqual(120, goal.Chain.CountdownSeconds);
            Assert.IsTrue(goal.Chain.ToAnotherTown);
            Assert.AreEqual("sky_rift", goal.Chain.NextSource);
            Assert.AreEqual(8, goal.Chain.Pacing.AnnounceSeconds);
            Assert.AreEqual(250, goal.TownReach.MaxWalk);

            var next = Stage2(goal);
            Assert.AreEqual(WorldEvent.DefaultMinDurationSeconds, next.MinDurationSeconds);
            Assert.AreEqual(WorldEvent.DefaultMaxDurationSeconds, next.MaxDurationSeconds);
            Assert.AreEqual(0, next.FamilyIds.Count, "the family rolls");
            Assert.AreEqual(WorldEvent.DefaultAnnounceLeadSeconds, next.AnnounceLeadSeconds);
            Assert.AreEqual(120 + 8 - WorldEvent.DefaultAnnounceLeadSeconds, next.TeaserLeadSeconds);

            Assert.AreEqual(WorldEventChain.Decision.Next,
                WorldEventChain.ApplySettings(WorldEventChain.Decision.Next, 1, true, 1.0, 0.99999), "chance 1 always leads on");

            foreach (var s in HoldSettings.InGroup(HoldSettings.GroupChain).Where(s => !s.IsReused))
                Assert.IsFalse(HoldSettings.Describe(s, goal).Overridden, s.Key);
        }

        [TestMethod]
        public void Chain_OffAndChance()
        {
            Assert.AreEqual(WorldEventChain.Decision.EndedAtMax,
                WorldEventChain.ApplySettings(WorldEventChain.Decision.Next, 1, false, 1.0, 0), "off: one and done, with the closing lines");
            Assert.AreEqual(WorldEventChain.Decision.EndedAtMax,
                WorldEventChain.ApplySettings(WorldEventChain.Decision.Next, 1, true, 0.25, 0.5), "the roll missed");
            Assert.AreEqual(WorldEventChain.Decision.Next,
                WorldEventChain.ApplySettings(WorldEventChain.Decision.Next, 1, true, 0.25, 0.1), "the roll hit");
            Assert.AreEqual(WorldEventChain.Decision.EndedByOutcome,
                WorldEventChain.ApplySettings(WorldEventChain.Decision.EndedByOutcome, 1, false, 0, 0), "a loss stays a loss");
            Assert.AreEqual(WorldEventChain.Decision.None,
                WorldEventChain.ApplySettings(WorldEventChain.Decision.None, 1, true, 1, 0), "no chain stays none");
        }

        [TestMethod]
        public void Chain_SettingsAreReadAtStageEnd()
        {
            var goal = EnvoysGoal();

            using var scope = new HoldSettingsScope();

            scope.Set(HoldKeys.ChainMaxLength, "3");
            scope.Set(HoldKeys.ChainCountdownSeconds, "90");
            scope.Set(HoldKeys.ChainAnnounceSeconds, "0");
            scope.Set(HoldKeys.ChainTownNetwork, "off");
            scope.Set(HoldKeys.ChainNextFamily, "Grievver");
            scope.Set(HoldKeys.ChainArrivalSeconds, "900");
            scope.Set(HoldKeys.ChainAnnounceLeadSeconds, "10");
            scope.Set(HoldKeys.TownReach, "400");

            Assert.AreEqual(3, goal.Chain.MaxLength);
            Assert.IsFalse(goal.Chain.ToAnotherTown);
            Assert.AreEqual(400, goal.TownReach.MaxWalk);

            var next = Stage2(goal);
            CollectionAssert.AreEqual(new[] { "grievver" }, next.FamilyIds, "the family is pinned");
            Assert.AreEqual(900, next.MaxDurationSeconds, "the arrival time limit");
            Assert.AreEqual(10, next.AnnounceLeadSeconds);
            Assert.AreEqual(80, next.TeaserLeadSeconds, "90 s countdown, no announce delay, 10 s of announce");

            scope.Set(HoldKeys.ChainNextFamily, "random");
            Assert.AreEqual(0, Stage2(goal).FamilyIds.Count);

            scope.Set(HoldKeys.ChainArrivalSeconds, "200");
            var shortStage = Stage2(goal);
            Assert.IsTrue(shortStage.MinDurationSeconds < shortStage.MaxDurationSeconds, "the champion still arrives inside a short limit");
        }

        [TestMethod]
        public void Chain_AxisValuesAreLive()
        {
            var goal = EnvoysGoal();

            using var scope = new HoldSettingsScope();

            scope.Set(HoldKeys.ChainNextSource, "random");
            scope.Set(HoldKeys.ChainNextBoss, "vhaleth");

            Assert.AreEqual("random", goal.Chain.NextSource);
            Assert.IsNull(Stage2(goal).SourceId, "random rolls");
            Assert.AreEqual("vhaleth", Stage2(goal).BossId, "a forced champion");
        }
    }
}
