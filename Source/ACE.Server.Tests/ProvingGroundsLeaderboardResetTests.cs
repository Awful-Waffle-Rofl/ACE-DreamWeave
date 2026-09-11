using System.Linq;

using ACE.Entity.Enum.Properties;
using ACE.Server.Command.Handlers;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Guards the /resetleaderboard board-name-to-property mapping used by the Proving Grounds leaderboard reset
    /// admin command. Asserts against the literal property ids (not the enum members) so a silent renumber of
    /// PropertyInt64 fails this test the same way WaveChallengePropertyTests catches a renumber of the wave
    /// engine's own properties. Pure static-method reflection - no database, no world.
    /// </summary>
    [TestClass]
    public class ProvingGroundsLeaderboardResetTests
    {
        [TestMethod]
        public void Dps_MapsToBestDpsScore_9017()
        {
            Assert.IsTrue(ProvingGroundsAdminCommands.TryGetBoards("dps", out var boards));
            Assert.AreEqual(1, boards.Count);
            Assert.AreEqual(9017, (int)boards[0].Property);
            Assert.AreEqual(9017, (int)PropertyInt64.BestDpsScore);
        }

        [TestMethod]
        public void Defense_MapsToBestSurvivalScore_9020()
        {
            Assert.IsTrue(ProvingGroundsAdminCommands.TryGetBoards("defense", out var boards));
            Assert.AreEqual(1, boards.Count);
            Assert.AreEqual(9020, (int)boards[0].Property);
            Assert.AreEqual(9020, (int)PropertyInt64.BestSurvivalScore);
        }

        [TestMethod]
        public void Wave_MapsToBestWaveScoreCenti_9022_ThenLegacy_9021()
        {
            Assert.IsTrue(ProvingGroundsAdminCommands.TryGetBoards("wave", out var boards));
            Assert.AreEqual(2, boards.Count);
            Assert.AreEqual(9022, (int)boards[0].Property);
            Assert.AreEqual(9021, (int)boards[1].Property);
            Assert.AreEqual(9022, (int)PropertyInt64.BestWaveScoreCenti);
            Assert.AreEqual(9021, (int)PropertyInt64.BestWaveScore);
        }

        [TestMethod]
        public void All_ReturnsExactlyTheFourBoards_InOrder()
        {
            Assert.IsTrue(ProvingGroundsAdminCommands.TryGetBoards("all", out var boards));
            Assert.AreEqual(4, boards.Count);

            var properties = boards.Select(b => (int)b.Property).ToList();
            CollectionAssert.AreEqual(new[] { 9017, 9020, 9022, 9021 }, properties);
        }

        [TestMethod]
        public void BoardArgument_IsCaseInsensitive()
        {
            Assert.IsTrue(ProvingGroundsAdminCommands.TryGetBoards("DPS", out var upper));
            Assert.AreEqual(9017, (int)upper[0].Property);

            Assert.IsTrue(ProvingGroundsAdminCommands.TryGetBoards("Wave", out var mixed));
            Assert.AreEqual(9022, (int)mixed[0].Property);

            Assert.IsTrue(ProvingGroundsAdminCommands.TryGetBoards("ALL", out var all));
            Assert.AreEqual(4, all.Count);
        }

        [TestMethod]
        public void UnknownArgument_ReturnsFalse()
        {
            Assert.IsFalse(ProvingGroundsAdminCommands.TryGetBoards("bogus", out var boards));
            Assert.IsNull(boards);
        }

        [TestMethod]
        public void NullArgument_ReturnsFalse()
        {
            Assert.IsFalse(ProvingGroundsAdminCommands.TryGetBoards(null, out var boards));
            Assert.IsNull(boards);
        }
    }
}
