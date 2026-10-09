using System.Collections.Generic;

namespace ACE.Server.ClassAbilities
{
    /// <summary>
    /// The enlightenment-milestone lane of the class-ability-point economy (DESIGN.md sec 2c): a character
    /// earns one class ability point at each of a fixed schedule of enlightenment counts - ENL 1, 2, 3, 4, 5,
    /// 7, 10, 15, 20, 25, 30, 35, 40, 45, 50, then every 10 beyond 50 (60, 70, 80, ...) indefinitely.
    /// Cumulatively: 5 CAPs by ENL 5, 7 by ENL 10, 15 by ENL 50, 16 at ENL 60, and so on. The lane is
    /// throttled by the time it takes to re-level after each enlightenment, not by luminance, so it stays a
    /// slow trickle for even the most dedicated players.
    ///
    /// This type is the PURE, side-effect-free core of that rule so it can be unit-tested without a live
    /// <see cref="WorldObjects.Player"/>. The grant itself (crediting the point, persisting the paid-out
    /// counter) lives in <c>Player.GrantEnlightenmentClassAbilityPoints</c>, which is an idempotent catch-up:
    /// it compares <see cref="EntitledCount"/> for the character's current enlightenment against the persisted
    /// count already paid and grants the difference. That makes it retroactive-safe for characters enlightened
    /// before this lane existed and immune to missed grants.
    /// </summary>
    public static class EnlightenmentCapMilestones
    {
        /// <summary>
        /// The fixed head of the schedule (enlightenment counts at which a class ability point is granted),
        /// ascending. Past the last entry (50), a point is granted every 10 enlightenments (60, 70, ...).
        /// </summary>
        public static readonly IReadOnlyList<int> Schedule =
            new[] { 1, 2, 3, 4, 5, 7, 10, 15, 20, 25, 30, 35, 40, 45, 50 };

        private const int TailStart = 50;
        private const int TailStep = 10;

        /// <summary>
        /// How many enlightenment-milestone points a character at <paramref name="enlightenment"/> is entitled
        /// to in total: the number of fixed <see cref="Schedule"/> entries at or below their enlightenment,
        /// plus one for every <see cref="TailStep"/> enlightenments past <see cref="TailStart"/>.
        /// </summary>
        public static int EntitledCount(int enlightenment)
        {
            if (enlightenment < 0)
                enlightenment = 0;

            var count = 0;

            foreach (var milestone in Schedule)
            {
                if (enlightenment >= milestone)
                    count++;
                else
                    break; // Schedule is ascending, so nothing further can qualify
            }

            // past the fixed schedule: +1 per TailStep enlightenments beyond TailStart (60, 70, 80, ...)
            if (enlightenment > TailStart)
                count += (enlightenment - TailStart) / TailStep;

            return count;
        }

        /// <summary>
        /// The smallest schedule milestone strictly greater than <paramref name="enlightenment"/>. Always
        /// exists (the schedule is infinite via the every-<see cref="TailStep"/> tail past
        /// <see cref="TailStart"/>), so this is used to tell a player when their next enlightenment class
        /// ability point will arrive.
        /// </summary>
        public static int NextMilestoneAfter(int enlightenment)
        {
            if (enlightenment < 0)
                enlightenment = 0;

            foreach (var milestone in Schedule)
                if (milestone > enlightenment)
                    return milestone;

            // past the fixed schedule: the next tail milestone (TailStart + k*TailStep, k >= 1) above the argument
            return ((enlightenment - TailStart) / TailStep + 1) * TailStep + TailStart;
        }
    }
}
