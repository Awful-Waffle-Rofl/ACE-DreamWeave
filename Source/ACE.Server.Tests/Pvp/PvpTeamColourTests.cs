using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Pvp;
using ACE.Server.Pvp.Battlegrounds;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>The colour rules (Docs/Pvp/TEMPLATES.md "Team hue", owner rulings 2026-10-15). Pure.</summary>
    [TestClass]
    public class PvpTeamColourTests
    {
        private static readonly PvpTeamColour Red = PvpTeamColour.Red, Blue = PvpTeamColour.Blue;

        [TestMethod]
        public void RuleFor_MapsTheModeKeys()
        {
            Assert.AreEqual(PvpColourRule.Tugak, PvpTeamColours.RuleFor(ArenaMapCatalog.FfaKey));
            Assert.AreEqual(PvpColourRule.AttackDefend, PvpTeamColours.RuleFor(BattlegroundModes.AttackDefendModeKey));
            Assert.AreEqual(PvpColourRule.CoinFlip, PvpTeamColours.RuleFor(BattlegroundModes.KothModeKey));
            Assert.AreEqual(PvpColourRule.CoinFlip, PvpTeamColours.RuleFor(ArenaMapCatalog.OneVOneKey));
            Assert.AreEqual(PvpColourRule.CoinFlip, PvpTeamColours.RuleFor(ArenaMapCatalog.TwoVTwoKey));
            Assert.AreEqual(PvpColourRule.CoinFlip, PvpTeamColours.RuleFor("bg_some_future_mode"), "any other mode is a coin flip");
            Assert.AreEqual(PvpColourRule.CoinFlip, PvpTeamColours.RuleFor(null));
        }

        /// <summary>1v1, 2v2, King of the Hill and any future two-team mode: the coin decides which team is Red; the other is Blue.</summary>
        [TestMethod]
        public void TwoTeamModes_CoinPicksWhichTeamIsRed_BothOutcomes()
        {
            foreach (var mode in new[] { ArenaMapCatalog.OneVOneKey, ArenaMapCatalog.TwoVTwoKey, BattlegroundModes.KothModeKey, "bg_future" })
            {
                Assert.AreEqual(Red, PvpTeamColours.Assign(mode, 2, 0, coin: true), mode);
                Assert.AreEqual(Blue, PvpTeamColours.Assign(mode, 2, 1, coin: true), mode);
                Assert.AreEqual(Blue, PvpTeamColours.Assign(mode, 2, 0, coin: false), mode);
                Assert.AreEqual(Red, PvpTeamColours.Assign(mode, 2, 1, coin: false), mode);
            }
        }

        /// <summary>Attack/Defend: attackers (team 0) Red, defenders Blue, whichever way the coin fell.</summary>
        [TestMethod]
        public void AttackDefend_AttackersRed_DefendersBlue_CoinIgnored()
        {
            foreach (var coin in new[] { true, false })
            {
                Assert.AreEqual(Red, PvpTeamColours.Assign(BattlegroundModes.AttackDefendModeKey, 2, 0, coin));
                Assert.AreEqual(Blue, PvpTeamColours.Assign(BattlegroundModes.AttackDefendModeKey, 2, 1, coin));
            }
        }

        /// <summary>Tugak Brawl: every seat the same colour for the match, and the coin picks which, at any field size.</summary>
        [TestMethod]
        public void Tugak_EverySeatTheSameColour_CoinPicksIt()
        {
            foreach (var teams in new[] { 2, 3, 5, 8 })
            {
                CollectionAssert.AreEqual(Enumerable.Repeat(Red, teams).ToList(), Enumerable.Range(0, teams).Select(i => PvpTeamColours.Assign(ArenaMapCatalog.FfaKey, teams, i, coin: true)).ToList(), $"{teams} teams, coin true");
                CollectionAssert.AreEqual(Enumerable.Repeat(Blue, teams).ToList(), Enumerable.Range(0, teams).Select(i => PvpTeamColours.Assign(ArenaMapCatalog.FfaKey, teams, i, coin: false)).ToList(), $"{teams} teams, coin false");
            }
        }

        /// <summary>A team count other than two (and not Tugak) takes the family default, Red, for everyone.</summary>
        [TestMethod]
        public void OtherTeamCounts_AreRed()
        {
            foreach (var rule in new[] { PvpColourRule.CoinFlip, PvpColourRule.AttackDefend })
                foreach (var count in new[] { 1, 3, 4 })
                    foreach (var coin in new[] { true, false })
                        for (var i = 0; i < count; i++)
                            Assert.AreEqual(Red, PvpTeamColours.Assign(rule, count, i, coin), $"{rule} {count} teams, team {i}, coin {coin}");
        }

        [TestMethod]
        public void NamesTokensAndParse()
        {
            Assert.AreEqual("Red", PvpTeamColours.Name(Red));
            Assert.AreEqual("Blue", PvpTeamColours.Name(Blue));
            Assert.AreEqual("red", PvpTeamColours.Token(Red));
            Assert.AreEqual("blue", PvpTeamColours.Token(Blue));
            Assert.AreEqual(Blue, PvpTeamColours.Other(Red));

            Assert.IsTrue(PvpTeamColours.TryParse(" BLUE ", out var c) && c == Blue);
            Assert.IsTrue(PvpTeamColours.TryParse("red", out c) && c == Red);
            Assert.IsFalse(PvpTeamColours.TryParse("green", out _));
            Assert.IsFalse(PvpTeamColours.TryParse(null, out _));
        }
    }
}
