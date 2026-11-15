using System.Collections.Generic;
using System.Linq;

using ACE.Server.Frays;
using ACE.Server.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>Frays creatures built as Thread creatures: the pure parts of FraysThreadsShape.</summary>
    [TestClass]
    public class FraysThreadsShapeTests
    {
        [TestMethod]
        public void RunLevel_StaysInsideThreadsRunCeiling()
        {
            Assert.AreEqual(441, FraysThreadsShape.RunLevel(441));
            Assert.AreEqual(DungeonGemSpec.MaxRunLevel, FraysThreadsShape.RunLevel(560));
            Assert.AreEqual(1, FraysThreadsShape.RunLevel(0));
        }

        [TestMethod]
        public void Roles_MapToThreadsRoles_SnarlAsElite()
        {
            Assert.AreEqual(DungeonRole.Trash, FraysThreadsShape.RoleFor(FraysRole.Swarm));
            Assert.AreEqual(DungeonRole.Trash, FraysThreadsShape.RoleFor(FraysRole.Standard));
            Assert.AreEqual(DungeonRole.Elite, FraysThreadsShape.RoleFor(FraysRole.Elite));
            Assert.AreEqual(DungeonRole.Elite, FraysThreadsShape.RoleFor(FraysRole.Snarl), "a Snarl is never shaped as a Threads boss");
        }

        [TestMethod]
        public void EntriesFor_FallsBackToTrash_WhenThePlanHasNoElite()
        {
            var trash = new DungeonSpawnPlanEntry(1, DungeonRole.Trash, null);
            var elite = new DungeonSpawnPlanEntry(2, DungeonRole.Elite, null);
            var boss = new DungeonSpawnPlanEntry(3, DungeonRole.Boss, null);

            CollectionAssert.AreEqual(new[] { 2u }, FraysThreadsShape.EntriesFor(new List<DungeonSpawnPlanEntry> { trash, elite, boss }, DungeonRole.Elite).Select(e => e.Wcid).ToArray());
            CollectionAssert.AreEqual(new[] { 1u }, FraysThreadsShape.EntriesFor(new List<DungeonSpawnPlanEntry> { trash, boss }, DungeonRole.Elite).Select(e => e.Wcid).ToArray());
            Assert.AreEqual(0, FraysThreadsShape.EntriesFor(null, DungeonRole.Trash).Count);
        }

        [TestMethod]
        public void EntryFor_UpliftsTheFamilysOwnPick_ToTheBandAtTheRunLevel()
        {
            // The band uplift comes first (skills to the band's medians); the stamp alone would leave a level 115
            // Monouga at 548 attack under a 480 run.
            var uplift = FraysThreadsShape.EntryFor(true, true, 24291, FraysRole.Elite, 480);
            Assert.AreEqual(24291u, uplift.Wcid);
            Assert.AreEqual(DungeonRole.Elite, uplift.Role);
            Assert.AreEqual(480, uplift.UpliftLevel, "band uplift, as Threads does for a pick below its band");
            Assert.AreEqual(0, uplift.StampLevel);

            var stamp = FraysThreadsShape.EntryFor(true, false, 24291, FraysRole.Standard, 300);
            Assert.AreEqual(300, stamp.StampLevel, "reach-up only when there is no band standard");
            Assert.AreEqual(0, stamp.UpliftLevel);

            Assert.IsNull(FraysThreadsShape.EntryFor(false, false, 24291, FraysRole.Standard, 300));
            Assert.IsNull(FraysThreadsShape.EntryFor(true, true, 0, FraysRole.Standard, 300));
        }
    }
}
