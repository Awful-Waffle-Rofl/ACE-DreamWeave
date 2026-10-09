namespace ACE.Server.Pvp
{
    /// <summary>
    /// Player-facing text and timing for the pvp_safe_landblocks combat-free zone (Docs/Runbook.md
    /// "pvp_safe_landblocks"), kept in one constant the same way PvpArenaText keeps the arena's strings.
    /// </summary>
    public static class PvpSafeZoneText
    {
        /// <summary>
        /// Sent to the attacker when a harmful action is refused by the safe-zone gate. Deliberately does not
        /// name the Marketplace - pvp_safe_landblocks is a configurable list and can be pointed elsewhere.
        /// </summary>
        public const string CombatFreeZone = "This area is a combat-free zone.";

        /// <summary>How often CombatFreeZone may repeat per attacker, same throttle shape as PvpArenaHookSettings.FriendlyFireNoticeIntervalSeconds.</summary>
        public const double NoticeIntervalSeconds = 10.0;
    }
}
