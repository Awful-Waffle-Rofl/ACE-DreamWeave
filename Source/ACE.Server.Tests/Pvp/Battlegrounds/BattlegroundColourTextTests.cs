using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Pvp;
using ACE.Server.Pvp.Battlegrounds;

namespace ACE.Server.Tests.Pvp.Battlegrounds
{
    /// <summary>
    /// Team colours in battleground and arena text (Docs/Pvp/TEMPLATES.md "Team hue"): King of the Hill names its teams Red/Blue by
    /// the colour each team was GIVEN (never by index), Attack/Defend keeps Attackers/Defenders and adds the colour to the role line, and
    /// the lineup tags each side. Pure.
    /// </summary>
    [TestClass]
    public class BattlegroundColourTextTests
    {
        private static KothTickHandler Koth(PvpTeamColour team0, PvpTeamColour team1)
        {
            var handler = new KothTickHandler(BattlegroundTunables.Defaults, new KothZone(0, 0, 0, 6, 3));
            handler.BindTeamColours(new Dictionary<int, PvpTeamColour> { [0] = team0, [1] = team1 });
            return handler;
        }

        /// <summary>Unbound, King of the Hill still says West/East exactly as before.</summary>
        [TestMethod]
        public void Koth_Unbound_KeepsWestEast()
        {
            var handler = new KothTickHandler(BattlegroundTunables.Defaults, new KothZone(0, 0, 0, 6, 3));

            Assert.AreEqual("West", handler.TeamName(0));
            Assert.AreEqual("East", handler.TeamName(1));
            Assert.AreEqual(" Score: West 3, East 1, first to 300 wins.", handler.StatusLine(new Dictionary<int, int> { [0] = 3, [1] = 1 }, 300));
        }

        /// <summary>
        /// DISCRIMINATES colour over index: with team 0 BLUE, team 0 is named Blue, the score line still puts Red first, and the winner line
        /// names the winning team by its colour.
        /// </summary>
        [TestMethod]
        public void Koth_NamesFollowTheColour_NotTheIndex()
        {
            var flipped = Koth(PvpTeamColour.Blue, PvpTeamColour.Red);

            Assert.AreEqual("Blue", flipped.TeamName(0));
            Assert.AreEqual("Red", flipped.TeamName(1));

            var scores = new Dictionary<int, int> { [0] = 120, [1] = 45 };
            Assert.AreEqual(" Score: Red 45, Blue 120, first to 300 wins.", flipped.StatusLine(scores, 300), "Red first, each score with its own team");

            var win = flipped.ResultLine(new MatchOutcome(new HashSet<int> { 0 }, new[] { 1, 2 }, false, true, EndReason.Score), scores);
            Assert.AreEqual("[Battleground] Blue has taken the hill! Final score: Red 45, Blue 120.", win);

            var straight = Koth(PvpTeamColour.Red, PvpTeamColour.Blue);
            Assert.AreEqual("Red", straight.TeamName(0));
            Assert.AreEqual(" Score: Red 120, Blue 45, first to 300 wins.", straight.StatusLine(scores, 300));
            Assert.AreEqual("[Battleground] Time is up. The match is a draw, Red 120, Blue 45.", straight.ResultLine(new MatchOutcome(new HashSet<int>(), new[] { 1, 1 }, true, true, EndReason.Timeout), scores));
        }

        [TestMethod]
        public void TeamFellowshipName_FromAColourName()
        {
            Assert.AreEqual("Red Team", BattlegroundText.TeamFellowshipName(Koth(PvpTeamColour.Red, PvpTeamColour.Blue).TeamName(0)));
            Assert.AreEqual("Blue Team", BattlegroundText.TeamFellowshipName(Koth(PvpTeamColour.Blue, PvpTeamColour.Red).TeamName(0)));
        }

        [TestMethod]
        public void AttackDefend_RoleLines_CarryTheColour_TeamNamesDoNot()
        {
            Assert.AreEqual("[Battleground] You are an Attacker (Red). Destroy the Warding Crystal before time runs out!", BattlegroundText.RoleAttacker(1, null, "Red"));
            Assert.AreEqual("[Battleground] You are a Defender (Blue). Protect the Warding Crystals until time runs out!", BattlegroundText.RoleDefender(3, null, "Blue"));
            Assert.AreEqual("[Battleground] You are an Attacker. Destroy the Warding Crystal before time runs out!", BattlegroundText.RoleAttacker(1), "no colour: unchanged");

            var handler = new AttackDefendTickHandler(BattlegroundTunables.Defaults);
            handler.BindTeamColours(new Dictionary<int, PvpTeamColour> { [0] = PvpTeamColour.Red, [1] = PvpTeamColour.Blue });
            Assert.AreEqual("Attackers", handler.TeamName(0), "the role stays the team name");
            Assert.AreEqual("Defenders", handler.TeamName(1));
        }

        [TestMethod]
        public void LineupTags()
        {
            Assert.AreEqual("Red team: Alpha (Duelist) and Charlie (Mage)", PvpArenaText.LineupTeam(PvpArenaText.LineupColourTeamTag(PvpTeamColour.Red), "Alpha (Duelist) and Charlie (Mage)"));
            Assert.AreEqual("Attackers (Red): Alpha (Duelist)", PvpArenaText.LineupTeam(PvpArenaText.LineupModeTeamTag("Attackers", PvpTeamColour.Red), "Alpha (Duelist)"));
            Assert.AreEqual("Alpha (Duelist)", PvpArenaText.LineupTeam(null, "Alpha (Duelist)"));
        }
    }
}
