using System;

namespace ACE.Server.Pvp
{
    /// <summary>
    /// The pure PvP damage-permission decision (Docs/Pvp/DESIGN.md "Damage gate"): "Every player-vs-player
    /// action is refused unless both players are in the same match and it is Live. Harm to a teammate is
    /// refused unless pvp_arena_friendly_fire is on." NotApplicable means neither side is in a match, so
    /// H6's caller should fall through to the ordinary retail PK rules instead.
    ///
    /// One exception to "Live only": a Countdown vulnerability spell (PvpArenaSpellRules.IsCountdownVuln: the three
    /// defense-lowering Creature Enchantments, the seven Life Magic elemental vulnerabilities and Imperil) may be cast during the pre-match Countdown (both
    /// sides Countdown, or one Countdown and one Live), so players can set up debuffs before the bell. This applies to arena matches only (1v1, 2v2, FFA); in a battleground Countdown everything stays refused. No damage can be dealt:
    /// melee, missile and every other spell stay refused until both sides are Live.
    /// </summary>
    public static class PvpArenaGate
    {
        /// <summary>
        /// TRUE when <paramref name="defender"/> is inside a battleground spawn-protection window at <paramref name="nowUtc"/> and
        /// <paramref name="attacker"/> is another player of the same match. The one test the gate and the DoT tick hook share.
        /// </summary>
        public static bool IsSpawnProtected(PvpPlayerBinding attacker, PvpPlayerBinding defender, DateTime nowUtc, ACE.Entity.Position defenderAt = null)
        {
            if (!IsBound(attacker) || !IsBound(defender) || ReferenceEquals(attacker, defender))
                return false;

            return attacker.Match.MatchId == defender.Match.MatchId && defender.IsSpawnProtected(nowUtc, defenderAt);
        }

        /// <param name="nowUtc">
        /// The clock the spawn-protection window is read against, with <paramref name="defenderAt"/> (the defender's current position) for room-mode protection. Null means the caller has no clock and no window applies, so an
        /// older call site that does not pass one behaves exactly as before.
        /// </param>
        public static PvpGateDecision Evaluate(PvpPlayerBinding attacker, PvpPlayerBinding defender, bool friendlyFireEnabled, bool countdownVuln = false, DateTime? nowUtc = null, ACE.Entity.Position defenderAt = null)
        {
            var attackerBound = IsBound(attacker);
            var defenderBound = IsBound(defender);

            if (!attackerBound && !defenderBound)
                return PvpGateDecision.NotApplicable;

            if (!attackerBound || !defenderBound)
                return PvpGateDecision.Refuse;

            if (attacker.Match.MatchId != defender.Match.MatchId)
                return PvpGateDecision.Refuse;

            if (attacker.State != PvpMatchState.Live || defender.State != PvpMatchState.Live)
            {
                // ARENA matches only (owner ruling 2026-10-06): a battleground Countdown refuses everything, vulns included. Fail closed: only the three arena mode keys qualify, so a null, empty, room or unknown key is refused too.
                var preMatchVulnAllowed = countdownVuln
                    && ArenaMapCatalog.IsArenaModeKey(attacker.Match.ModeKey)
                    && (attacker.State == PvpMatchState.Countdown || attacker.State == PvpMatchState.Live)
                    && (defender.State == PvpMatchState.Countdown || defender.State == PvpMatchState.Live);

                if (!preMatchVulnAllowed)
                    return PvpGateDecision.Refuse;
            }

            // Battlegrounds (Docs/Pvp/BATTLEGROUNDS.md "Respawn" 7): a player who is dead or waiting in the pen can
            // neither harm nor be harmed. Always false for an arena binding.
            if (attacker.Respawning || defender.Respawning)
                return PvpGateDecision.Refuse;

            // Battlegrounds: a freshly respawned defender is untouchable for the window, teammates' friendly fire included.
            if (nowUtc.HasValue && IsSpawnProtected(attacker, defender, nowUtc.Value, defenderAt))
                return PvpGateDecision.RefuseProtected;

            if (attacker.TeamIndex == defender.TeamIndex && !friendlyFireEnabled)
                return PvpGateDecision.Refuse;

            return PvpGateDecision.Allow;
        }

        private static bool IsBound(PvpPlayerBinding binding)
        {
            return binding != null && binding.Match != null;
        }
    }
}
