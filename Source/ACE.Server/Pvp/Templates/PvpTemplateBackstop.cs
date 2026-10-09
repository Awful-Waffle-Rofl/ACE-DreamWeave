using System;

namespace ACE.Server.Pvp.Templates
{
    /// <summary>
    /// The heartbeat backstop's decision (TEMPLATES.md lifecycle, last trigger row): a templated player with no
    /// live match for pvp_template_backstop_seconds is restored. Pure, so the timing is unit tested.
    /// </summary>
    public static class PvpTemplateBackstop
    {
        /// <summary>
        /// Returns true when the player must be restored now. <paramref name="unboundSinceUtc"/> is the per-player
        /// "first seen templated without a match" stamp, carried between beats by the caller: it is set on the
        /// first such beat, cleared the moment the player is untemplated or back in a match, and the restore fires
        /// once it is at least <paramref name="delay"/> old.
        /// </summary>
        public static bool ShouldRestore(bool templated, bool inMatch, ref DateTime? unboundSinceUtc, DateTime nowUtc, TimeSpan delay)
        {
            if (!templated || inMatch)
            {
                unboundSinceUtc = null;
                return false;
            }

            if (unboundSinceUtc == null)
            {
                unboundSinceUtc = nowUtc;
                return false;
            }

            return nowUtc - unboundSinceUtc.Value >= delay;
        }
    }
}
