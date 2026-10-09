using System;
using System.Linq;

namespace ACE.Server.Pvp.Battlegrounds
{
    /// <summary>
    /// The Attack/Defend respawn policy (Docs/Pvp/ATTACK-DEFEND.md "Match flow"): the pen time depends on the dead player's side, so
    /// either side can be tuned live. Team 0 is the attacker; any other team index takes the defender delay. The side is found through
    /// <see cref="IMatchView.Teams"/>, so the policy never needs a seat lookup. Stateless and pure.
    /// </summary>
    public sealed class SidedRespawnPolicy : IRespawnPolicy
    {
        private readonly TimeSpan _attackerDelay;
        private readonly TimeSpan _defenderDelay;

        public SidedRespawnPolicy(int attackerSeconds, int defenderSeconds)
        {
            _attackerDelay = TimeSpan.FromSeconds(Math.Max(0, attackerSeconds));
            _defenderDelay = TimeSpan.FromSeconds(Math.Max(0, defenderSeconds));
        }

        public DeathDisposition OnDeath(IMatchView m, PvpParticipant p)
        {
            var team = m?.Teams?.FirstOrDefault(t => t.Members != null && t.Members.Contains(p));
            var delay = team != null && team.TeamIndex == CrystalWinCondition.AttackerTeam ? _attackerDelay : _defenderDelay;

            return DeathDisposition.Respawn(delay, BattlegroundRespawnPolicy.CoordinatorChoosesSpawn);
        }
    }
}
