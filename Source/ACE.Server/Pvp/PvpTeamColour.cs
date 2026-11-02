using System;

using ACE.Server.Pvp.Battlegrounds;

namespace ACE.Server.Pvp
{
    /// <summary>
    /// A team's colour for one match (Docs/Pvp/TEMPLATES.md "Team hue"). It picks the team hue the chosen
    /// template's worn armor is dressed in, and in two-team modes it names the team. Players never choose it.
    /// </summary>
    public enum PvpTeamColour
    {
        /// <summary>The Red team: dressed in the Red team hue, named Red in two-team modes.</summary>
        Red,

        Blue,
    }

    /// <summary>How a mode assigns colours (see <see cref="PvpTeamColours.Assign"/>).</summary>
    public enum PvpColourRule
    {
        /// <summary>Any two-team mode (1v1, 2v2, King of the Hill, any future one): a per-match coin flip picks which team is Red.</summary>
        CoinFlip,

        /// <summary>Attack/Defend: the attackers (team 0) are Red, the defenders Blue.</summary>
        AttackDefend,

        /// <summary>Tugak Brawl (FFA): every seat wears the SAME colour, picked by the per-match coin flip, so nobody reads teams into it.</summary>
        Tugak,
    }

    /// <summary>The colour rules (owner rulings 2026-10-15). Pure.</summary>
    public static class PvpTeamColours
    {
        /// <summary>The rule for <paramref name="modeKey"/>: Tugak for the FFA key, AttackDefend for bg_ad, a coin flip otherwise.</summary>
        public static PvpColourRule RuleFor(string modeKey)
        {
            if (string.Equals(modeKey, ArenaMapCatalog.FfaKey, StringComparison.OrdinalIgnoreCase))
                return PvpColourRule.Tugak;

            if (string.Equals(modeKey, BattlegroundModes.AttackDefendModeKey, StringComparison.OrdinalIgnoreCase))
                return PvpColourRule.AttackDefend;

            return PvpColourRule.CoinFlip;
        }

        /// <summary>
        /// The colour of team <paramref name="teamIndex"/> in a match of <paramref name="teamCount"/> teams under
        /// <paramref name="rule"/>, given the match's one coin flip (<paramref name="coin"/>, drawn once when the match forms).
        ///
        ///   - Tugak: every seat is Red when the coin is true, Blue when false, whatever the team count.
        ///   - Attack/Defend with two teams: the attackers (CrystalWinCondition.AttackerTeam) Red, the defenders Blue. The coin is not read.
        ///   - Coin flip with two teams: the coin true makes team 0 Red and team 1 Blue; false swaps them.
        ///   - Any other team count (not Tugak): Red for everyone, the family default.
        /// </summary>
        public static PvpTeamColour Assign(PvpColourRule rule, int teamCount, int teamIndex, bool coin)
        {
            if (rule == PvpColourRule.Tugak)
                return coin ? PvpTeamColour.Red : PvpTeamColour.Blue;

            if (teamCount != 2)
                return PvpTeamColour.Red;

            if (rule == PvpColourRule.AttackDefend)
                return teamIndex == CrystalWinCondition.AttackerTeam ? PvpTeamColour.Red : PvpTeamColour.Blue;

            var team0 = coin ? PvpTeamColour.Red : PvpTeamColour.Blue;

            return teamIndex == 0 ? team0 : Other(team0);
        }

        /// <summary><see cref="Assign(PvpColourRule, int, int, bool)"/> for a mode key.</summary>
        public static PvpTeamColour Assign(string modeKey, int teamCount, int teamIndex, bool coin) => Assign(RuleFor(modeKey), teamCount, teamIndex, coin);

        public static PvpTeamColour Other(PvpTeamColour colour) => colour == PvpTeamColour.Red ? PvpTeamColour.Blue : PvpTeamColour.Red;

        /// <summary>"Red" / "Blue": the player-facing word.</summary>
        public static string Name(PvpTeamColour colour) => colour == PvpTeamColour.Red ? "Red" : "Blue";

        /// <summary>"red" / "blue": the stored and typed form (the admin argument, the override setting).</summary>
        public static string Token(PvpTeamColour colour) => colour == PvpTeamColour.Red ? "red" : "blue";

        /// <summary>Parses "red" or "blue", any case, surrounding whitespace ignored.</summary>
        public static bool TryParse(string text, out PvpTeamColour colour)
        {
            switch (text?.Trim().ToLowerInvariant())
            {
                case "red":
                    colour = PvpTeamColour.Red;
                    return true;
                case "blue":
                    colour = PvpTeamColour.Blue;
                    return true;
                default:
                    colour = PvpTeamColour.Red;
                    return false;
            }
        }
    }
}
