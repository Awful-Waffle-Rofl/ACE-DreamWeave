using System.Collections.Generic;
using System.Linq;

using ACE.Server.WorldEvents;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>The messages group of the Hold settings.</summary>
    public partial class WorldEventHoldSettingsTests
    {
        [TestMethod]
        public void Messages_WithNothingSet_AreExactlyAsBefore()
        {
            var goal = EnvoysGoal();

            using var scope = new HoldSettingsScope();

            CollectionAssert.AreEqual(new[] { 75, 50, 25 }, HoldSettings.KnightMilestonePercents().ToList());
            Assert.AreEqual(HoldSettings.BroadcastWorld, HoldSettings.Choice(HoldKeys.Broadcast, HoldSettings.BroadcastWorld));

            var tokens = new WorldEventChain.StoryTokens { Envoy = "Sir Adarl", Champion = "Ssethaun", NextTown = "Rithwic" };
            Assert.IsNotNull(WorldEventChain.StoryLine(goal.Chain.Story, WorldEventChain.StoryBeat.Reveal, tokens, new System.Random(1)));

            foreach (var s in HoldSettings.InGroup(HoldSettings.GroupMessages).Where(s => !s.IsReused))
                Assert.IsFalse(HoldSettings.Describe(s, goal).Overridden, s.Key);
        }

        [TestMethod]
        public void Messages_StoryOff_KeepsOnlyTheThanks()
        {
            var goal = EnvoysGoal();

            using var scope = new HoldSettingsScope();
            scope.Set(HoldKeys.Story, "off");

            var tokens = new WorldEventChain.StoryTokens { Envoy = "Sir Adarl", Champion = "Ssethaun", NextTown = "Rithwic" };

            Assert.IsNull(WorldEventChain.StoryLine(goal.Chain.Story, WorldEventChain.StoryBeat.Reveal, tokens, new System.Random(1)));
            Assert.IsNull(WorldEventChain.StoryLine(goal.Chain.Story, WorldEventChain.StoryBeat.ClosingSuccess, tokens, new System.Random(1)));
            Assert.IsNotNull(WorldEventChain.StoryLine(goal.Chain.Story, WorldEventChain.StoryBeat.Thanks, tokens, new System.Random(1)), "the cache pointer stays");

            var plan = WorldEventChain.StageChangePlan(goal.Chain, tokens, "A rift opens!", new System.Random(1));
            Assert.AreEqual(2, plan.Count, "thanks and the announcement, no story beat");
        }

        [TestMethod]
        public void Messages_MilestonePercents()
        {
            CollectionAssert.AreEqual(new List<int> { 80, 60, 40, 20 }, HoldSettings.ParseMilestones("20, 40,80,60", out _));
            Assert.AreEqual(0, HoldSettings.ParseMilestones("none", out _).Count);
            Assert.IsNull(HoldSettings.ParseMilestones("75,150", out var problem));
            StringAssert.Contains(problem, "150");

            using var scope = new HoldSettingsScope();
            scope.Set(HoldKeys.MilestonePercents, "90,10");
            CollectionAssert.AreEqual(new[] { 90, 10 }, HoldSettings.KnightMilestonePercents().ToList());

            var fired = new HashSet<int>();
            CollectionAssert.AreEqual(new List<int> { 90 }, WorldEvent.DueBossHealthMilestones(8500, 10000, fired, HoldSettings.KnightMilestonePercents()));
            CollectionAssert.AreEqual(new List<int> { 75, 50 }, WorldEvent.DueBossHealthMilestones(4000, 10000, new HashSet<int>()), "bosses keep 75/50/25");

            scope.Set(HoldKeys.MilestonePercents, "nonsense");
            CollectionAssert.AreEqual(new[] { 75, 50, 25 }, HoldSettings.KnightMilestonePercents().ToList(), "unusable: as before");
        }
    }
}
