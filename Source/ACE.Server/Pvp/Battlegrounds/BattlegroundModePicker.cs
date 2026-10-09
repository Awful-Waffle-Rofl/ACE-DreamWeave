using System;
using System.Collections.Generic;

namespace ACE.Server.Pvp.Battlegrounds
{
    /// <summary>
    /// Picks which battleground mode a forming match plays (Docs/Pvp/ATTACK-DEFEND.md "Mode picking"). Every battleground mode shares the
    /// one "bg" room, so the matchmaker forms a match first and this decides its mode: a round-robin over the ENABLED modes, skipping any
    /// mode whose template is not offered to every seat of the match. When none qualifies, the mode offered to the MOST seats wins (ties
    /// in round-robin order from the cursor), so the fewest seats are withdrawn at dispatch; without a seat count, or when no mode is
    /// available at all, it falls back to the first enabled mode.
    ///
    /// <para/>
    /// The rotation is a cursor over the enabled list: each pick scans forward from the cursor for the next qualifying mode and leaves the
    /// cursor just after it, so a mode that is skipped now is not starved later. A fallback (most seats or first mode) does not move the
    /// cursor. One instance per coordinator; the world thread is its only caller. Pure apart from that one integer: no clock, no random.
    /// </summary>
    public sealed class BattlegroundModePicker
    {
        private int _cursor;

        /// <param name="enabled">The enabled modes, in catalogue order. Null or empty returns null.</param>
        /// <param name="qualifies">True when every seat of the match is offered a template for the given mode. Null means every mode qualifies.</param>
        /// <param name="seatsOffered">
        /// For the fallback: how many of the match's seats the given mode is offered to, or a negative number when the mode cannot be
        /// played at all (it is then never picked by the fallback). Null keeps the first-enabled-mode fallback.
        /// </param>
        public PvpModeDefinition Pick(IReadOnlyList<PvpModeDefinition> enabled, Func<PvpModeDefinition, bool> qualifies = null, Func<PvpModeDefinition, int> seatsOffered = null)
        {
            if (enabled == null || enabled.Count == 0)
                return null;

            for (var step = 0; step < enabled.Count; step++)
            {
                var i = (_cursor + step) % enabled.Count;

                if (qualifies == null || qualifies(enabled[i]))
                {
                    _cursor = (i + 1) % enabled.Count;
                    return enabled[i];
                }
            }

            if (seatsOffered != null)
            {
                PvpModeDefinition best = null;
                var bestSeats = -1;

                // Round-robin order from the cursor, and a strictly-greater test, so the first of equally good modes wins a tie.
                for (var step = 0; step < enabled.Count; step++)
                {
                    var mode = enabled[(_cursor + step) % enabled.Count];
                    var seats = seatsOffered(mode);

                    if (seats >= 0 && seats > bestSeats)
                    {
                        best = mode;
                        bestSeats = seats;
                    }
                }

                if (best != null)
                    return best;
            }

            return enabled[0];
        }
    }
}
