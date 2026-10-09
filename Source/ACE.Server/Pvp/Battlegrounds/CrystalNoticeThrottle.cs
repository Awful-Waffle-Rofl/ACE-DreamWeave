using System;

namespace ACE.Server.Pvp.Battlegrounds
{
    /// <summary>
    /// The per-player rate limit on the crystal refusal line (Docs/Pvp/ATTACK-DEFEND.md "Who may damage it"): an AoE, a cleave or a
    /// multi-shot asks the gate about every creature in range at once, so a refused attacker would otherwise get a line per hit. One
    /// instance per player, owned by that player and touched only on their landblock thread, so one attacker's refusals never throttle
    /// another's. Pure: the caller supplies the clock.
    /// </summary>
    public sealed class CrystalNoticeThrottle
    {
        private DateTime _lastUtc;

        /// <summary>
        /// True, and starts the window, when no line was sent within the last <paramref name="intervalSeconds"/> seconds; false (and the
        /// window untouched) otherwise.
        /// </summary>
        public bool TryAcquire(DateTime nowUtc, double intervalSeconds)
        {
            if ((nowUtc - _lastUtc).TotalSeconds < intervalSeconds)
                return false;

            _lastUtc = nowUtc;
            return true;
        }
    }
}