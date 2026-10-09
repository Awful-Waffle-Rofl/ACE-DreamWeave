using System;

using ACE.Server.Pvp;
using ACE.Server.Pvp.Battlegrounds;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// Attack/Defend engaged defenders (Docs/Pvp/ATTACK-DEFEND.md "Engaged defenders"): when this player last dealt or took damage against
    /// an opposing player of the same Live match, and the sample the crystal damage sink reads. In memory only, never saved, scoped by match
    /// id so a stamp from an earlier match never counts in the next.
    /// </summary>
    partial class Player
    {
        private volatile PvpEngagementStamp pvpEngagement;

        /// <summary>The last engagement stamp, or null when this player has not fought an opposing match player since login.</summary>
        internal PvpEngagementStamp PvpEngagement => pvpEngagement;

        /// <summary>Replaces the engagement stamp (one reference swap).</summary>
        internal void StampPvpEngagement(Guid matchId, DateTime atUtc) => pvpEngagement = new PvpEngagementStamp(matchId, atUtc);

        /// <summary>
        /// Called from DamageHistory.Add for every damaging health write that applied at least one point (melee, missile, spell projectile,
        /// life magic, DoT ticks): when <paramref name="victim"/> and <paramref name="attacker"/> are two players bound to the same Live
        /// match on different teams (<see cref="CrystalDefenderReduction.CountsAsEngagement"/>), stamps BOTH, so one site records damage
        /// dealt and damage taken. A crystal, an NPC, a pet, a teammate, or a match that is not Live stamps nothing. Two type tests and a
        /// return for every other hit.
        /// </summary>
        internal static void OnPvpDamageRecorded(Creature victim, WorldObject attacker)
        {
            if (victim is not Player target || attacker is not Player source || ReferenceEquals(target, source))
                return;

            var targetBinding = target.PvpBinding;
            var sourceBinding = source.PvpBinding;

            if (!CrystalDefenderReduction.CountsAsEngagement(targetBinding, sourceBinding))
                return;

            var now = PvpArenaHookSettings.UtcNow();
            var matchId = targetBinding.Match.MatchId;

            target.StampPvpEngagement(matchId, now);
            source.StampPvpEngagement(matchId, now);
        }

        /// <summary>
        /// This player as a candidate defender for the engaged-defender reduction: binding, alive (not dead and not in the death process),
        /// position and engagement stamp. Read on the crystal's landblock thread, which is this player's own while they stand in the match copy.
        /// </summary>
        internal DefenderSample ToDefenderSample() => new DefenderSample(PvpBinding, !IsDead && !IsInDeathProcess, Location, pvpEngagement);
    }
}
