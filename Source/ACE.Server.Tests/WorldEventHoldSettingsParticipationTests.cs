using System.Linq;

using ACE.Server.WorldEvents;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>The participation group of the Hold settings.</summary>
    public partial class WorldEventHoldSettingsTests
    {
        [TestMethod]
        public void Participation_WithNothingSet_IsExactlyAsBefore()
        {
            var goal = EnvoysGoal();

            using var scope = new HoldSettingsScope();

            Assert.IsTrue(HoldSettings.MeetsMinPresence(null, 1));
            Assert.IsFalse(HoldSettings.Toggle(HoldKeys.AdminsCount, false));
            Assert.AreEqual(0, HoldSettings.Number(HoldKeys.AfkSeconds, 0));
            Assert.IsTrue(WorldEventAudienceSampler.CountsTowardAudience(null), "the staff rule is unchanged");
            Assert.IsFalse(WorldEventAudienceSampler.CountsTowardAudience(ACE.Entity.Enum.AccessLevel.Admin));

            foreach (var s in HoldSettings.InGroup(HoldSettings.GroupParticipation).Where(s => !s.IsReused))
                Assert.IsFalse(HoldSettings.Describe(s, goal).Overridden, s.Key);

            Assert.IsTrue(HoldSettings.TryFind(HoldKeys.ParticipantRadius, out var radius));
            Assert.AreEqual(HoldSettingSource.OtherFile, HoldSettings.Describe(radius, goal).Source, "the radius lives in sources.json");
        }

        [TestMethod]
        public void Participation_IdleRule()
        {
            Assert.IsFalse(WorldEventHoldRules.IsIdle(100, 150, 0), "0 = off");
            Assert.IsFalse(WorldEventHoldRules.IsIdle(100, 159, 60));
            Assert.IsTrue(WorldEventHoldRules.IsIdle(100, 160, 60));
        }
    }
}
