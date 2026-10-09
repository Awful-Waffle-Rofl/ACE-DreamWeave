using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Pvp;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>The arena currency's player-facing name (renamed from Blood on 2026-10-06): singular for exactly 1, plural otherwise.</summary>
    [TestClass]
    public class PvpArenaMarkTextTests
    {
        [DataTestMethod]
        [DataRow(1, "You receive 1 Mark of the Hopeslayer.")]
        [DataRow(3, "You receive 3 Marks of the Hopeslayer.")]
        [DataRow(0, "You receive 0 Marks of the Hopeslayer.")]
        public void PayoutLine_UsesTheSingularOnlyForOne(int amount, string expectedTail)
        {
            var line = PvpArenaText.Fill(PvpArenaText.BloodReceived, ("amount", amount), ("marks", PvpArenaText.MarkNoun(amount)));

            Assert.AreEqual("[Arena] " + expectedTail, line);
        }

        [DataTestMethod]
        [DataRow(1, "You receive 1 Mark of the Hopeslayer.")]
        [DataRow(3, "You receive 3 Marks of the Hopeslayer.")]
        public void BattlegroundPayoutLine_UsesTheSingularOnlyForOne(int amount, string expectedTail)
        {
            var line = PvpArenaText.Fill(PvpArenaText.BgMarksReceived, ("amount", amount), ("marks", PvpArenaText.MarkNoun(amount)));

            Assert.AreEqual("[Battleground] " + expectedTail, line);
        }

        [TestMethod]
        public void SharedBalanceLines_NeverSayArena()
        {
            Assert.IsFalse(PvpArenaText.Fill(PvpArenaText.BloodOwedDelivered, ("amount", 5), ("marks", PvpArenaText.MarkNoun(5))).Contains("arena"));
            Assert.IsFalse(PvpArenaText.Fill(PvpArenaText.BloodOwedRemaining, ("amount", 5), ("marks", PvpArenaText.MarkNoun(5))).Contains("arena"));
        }

        [TestMethod]
        public void EveryPlayerFacingPayoutLine_NamesTheMark_AndNoneLeavesAPlaceholderOrTheOldName()
        {
            var lines = new[]
            {
                PvpArenaText.Fill(PvpArenaText.BloodReceived, ("amount", 2), ("marks", PvpArenaText.MarkNoun(2))),
                PvpArenaText.BloodDailyLimit,
                PvpArenaText.Fill(PvpArenaText.BloodOwed, ("amount", 1), ("marks", PvpArenaText.MarkNoun(1))),
                PvpArenaText.Fill(PvpArenaText.BloodOwedDelivered, ("amount", 5), ("marks", PvpArenaText.MarkNoun(5))),
                PvpArenaText.Fill(PvpArenaText.BloodOwedRemaining, ("amount", 2), ("marks", PvpArenaText.MarkNoun(2))),
                PvpArenaText.Fill(PvpArenaText.BgMarksReceived, ("amount", 2), ("marks", PvpArenaText.MarkNoun(2))),
                PvpArenaText.BgMarksDailyLimit,
                PvpArenaText.Fill(PvpArenaText.BgMarksOwed, ("amount", 1), ("marks", PvpArenaText.MarkNoun(1))),
            };

            foreach (var line in lines)
            {
                StringAssert.Contains(line, "of the Hopeslayer");
                Assert.IsFalse(line.Contains("{"), line);
                Assert.IsFalse(line.Contains("Blood"), line);
            }
        }
    }
}