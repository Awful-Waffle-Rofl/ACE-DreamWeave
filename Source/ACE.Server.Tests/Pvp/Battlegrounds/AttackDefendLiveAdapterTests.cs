using System;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Pvp;
using ACE.Server.Pvp.Battlegrounds;

namespace ACE.Server.Tests.Pvp.Battlegrounds
{
    /// <summary>
    /// Phase B item 3 of Docs/Pvp/ATTACK-DEFEND.md "Placement": the parts of the live crystal adapter that run without a world. The
    /// create, tag and EnterWorld path itself needs a live landblock and is a live check.
    /// </summary>
    [TestClass]
    public class AttackDefendLiveAdapterTests
    {
        private static AttackDefendPlan Plan() => AttackDefendPlan.Build(BattlegroundMapCatalog.Bg003c, BattlegroundTunables.Defaults, 4);

        [TestMethod]
        public void TheLiveSpaces_ImplementTheObjectiveSeam()
        {
            // The coordinator offers Attack/Defend only when its spaces also implement IObjectiveMatchSpaces (found with `as`).
            Assert.IsTrue(typeof(IObjectiveMatchSpaces).IsAssignableFrom(typeof(LivePvpMatchSpaces)));
        }

        [TestMethod]
        public void OnlyTheMinesCarryACrystalWcid()
        {
            Assert.AreEqual(1006805u, BattlegroundMapCatalog.Bg003c.CrystalWcid, "the Warding Crystal");
            Assert.AreEqual(0u, BattlegroundMapCatalog.Bg016c.CrystalWcid, "King of the Hill places no crystal");
        }

        [TestMethod]
        public void ASpaceWithNoLiveLandblock_FailsTheJobAtOnce_AndPlacesNothing()
        {
            var space = new MatchSpace(BattlegroundMapCatalog.Bg003cKey, 0x003C, 1, 0, 1);
            var tags = 0;

            var job = LivePvpMatchSpaces.QueueObjectives(space, 1006805, Plan(), c => { tags++; return new BattlegroundObjectiveTag(Guid.NewGuid(), c.Index, 1); });

            Assert.AreEqual(BattlegroundFixtureStatus.Failed, job.Status);
            Assert.AreEqual(0, job.Placed);
            Assert.AreEqual(0, tags, "no crystal was created, so no tag was asked for");
        }

        [TestMethod]
        public void TheSample_IsEmptyUntilTheJobIsPlaced_AndTheRescaleIsANoOpWithoutALiveInstance()
        {
            var spaces = new LivePvpMatchSpaces();
            var space = new MatchSpace(BattlegroundMapCatalog.Bg003cKey, 0x003C, 1, 0, 1);

            Assert.AreEqual(0, spaces.SampleObjectives(space, null).Count);
            Assert.AreEqual(0, spaces.SampleObjectives(space, new BattlegroundFixtureJob()).Count, "a pending job samples nothing");

            spaces.ScaleObjectives(space, new BattlegroundFixtureJob(), 4, 3);
            spaces.ScaleObjectives(null, null, 4, 3);
        }
    }
}
