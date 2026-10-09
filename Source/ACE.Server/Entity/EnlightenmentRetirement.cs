using System.Collections.Generic;

namespace ACE.Server.Entity
{
    /// <summary>
    /// One-time restitution for characters who enlightened before the system was retired
    /// (Docs/ClassAbilities/XP-LANE-SPEC.md sec 5.2 / 5.2a).
    ///
    /// Enlightenment zeroed both <c>TotalExperience</c> and <c>AvailableExperience</c> on every cycle, so an
    /// enlightened character's own record carries no trace of the XP it spent - only the count survives. This
    /// type converts that count back into the level and the spendable pool the character would hold had it
    /// leveled continuously instead, which is the only reconstruction available once the resets are gone.
    ///
    /// PURE and side-effect-free so it is unit-testable without a live <see cref="WorldObjects.Player"/>; the
    /// live credit is <c>Player.GrantEnlightenmentRetirementCredit</c>.
    /// </summary>
    public static class EnlightenmentRetirement
    {
        /// <summary>
        /// The total XP a character actually spent to reach <paramref name="enlightenment"/>.
        ///
        /// Each enlightenment required standing at the personal cap of that cycle - 275 + 5 per enlightenment
        /// already held - starting from level 1 with a zeroed total, so the cost of cycle k is the FULL chart
        /// total for level 275 + 5k and the lifetime cost is their sum. Saturates rather than wrapping if the
        /// sum would exceed the chart's own ceiling, which is also the most a character can ever hold.
        /// </summary>
        public static ulong CumulativeXpSpent(int enlightenment, IReadOnlyList<ulong> totals)
        {
            if (enlightenment <= 0 || totals == null || totals.Count == 0)
                return 0;

            var ceiling = totals[totals.Count - 1];
            ulong cumulative = 0;

            for (var k = 0; k < enlightenment; k++)
            {
                var level = EnlightenmentXpCurve.BaseMaxLevel + EnlightenmentXpCurve.LevelsPerEnlightenment * k;

                // a character past the chart's ceiling could not have kept enlightening anyway
                if (level >= totals.Count)
                    return ceiling;

                var cost = totals[level];

                if (cumulative > ceiling - cost)
                    return ceiling;

                cumulative += cost;
            }

            return cumulative;
        }

        /// <summary>
        /// The level a continuously-leveling character would hold for the same XP: the smallest level whose
        /// cumulative chart total is at or above <see cref="CumulativeXpSpent"/>. Returns 0 for an
        /// unenlightened character, and clamps to the chart's last level.
        ///
        /// Sampled results against the shipped chart (XP-LANE-SPEC sec 5.2): ENL 1 -> 275, 2 -> 319, 5 -> 388,
        /// 10 -> 451, 20 -> 531, 50 -> 703. Past ENL 50 it converges on 5 * ENL + 456, which is structural -
        /// a geometric chart turns a constant XP factor per cycle into a constant level offset.
        /// </summary>
        public static int EquivalentLevel(int enlightenment, IReadOnlyList<ulong> totals)
        {
            if (enlightenment <= 0 || totals == null || totals.Count == 0)
                return 0;

            var target = CumulativeXpSpent(enlightenment, totals);

            for (var level = 0; level < totals.Count; level++)
            {
                if (totals[level] >= target)
                    return level;
            }

            return totals.Count - 1;
        }
    }
}
