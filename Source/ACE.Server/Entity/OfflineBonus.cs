using System;

namespace ACE.Server.Entity
{
    /// <summary>
    /// Pure math for the per-character "offline bonus" system. A character banks bonus time 1:1 with the
    /// time it spends logged out (capped), drains it 1:1 with time spent online, and while any remains its
    /// own combat XP/Luminance is scaled up by a configured multiplier. Kept free of <see cref="WorldObjects.Player"/>
    /// state so the boundary behavior (24h cap, floor at zero, overflow guard) is unit-testable in isolation.
    /// See Player_OfflineBonus.cs for how these are wired into login/logout/heartbeat and the XP grant path.
    /// </summary>
    public static class OfflineBonus
    {
        /// <summary>
        /// Adds <paramref name="offlineSeconds"/> of banked time to <paramref name="currentBank"/>, capped at
        /// <paramref name="maxSeconds"/>. A non-positive offline duration leaves the bank unchanged (but still
        /// clamped to the cap, in case the cap was lowered or the value was set out of band).
        /// </summary>
        public static double Accrue(double currentBank, double offlineSeconds, long maxSeconds)
        {
            if (currentBank < 0)
                currentBank = 0;

            if (offlineSeconds > 0)
                currentBank += offlineSeconds;

            return Math.Min(currentBank, maxSeconds);
        }

        /// <summary>
        /// Subtracts <paramref name="elapsedSeconds"/> of online time from <paramref name="currentBank"/>,
        /// floored at zero. Non-positive elapsed time (or an already-empty bank) leaves it unchanged.
        /// </summary>
        public static double Drain(double currentBank, double elapsedSeconds)
        {
            if (currentBank <= 0)
                return 0;

            if (elapsedSeconds <= 0)
                return currentBank;

            return Math.Max(0, currentBank - elapsedSeconds);
        }

        /// <summary>
        /// Scales <paramref name="amount"/> by (1 + <paramref name="multiplier"/>) when the character has bonus
        /// time remaining. Returns the amount unchanged when the bonus is inactive (no time left, non-positive
        /// multiplier, or a non-positive amount) or when the boosted value would overflow a long.
        /// </summary>
        public static long Apply(long amount, double remaining, double multiplier)
        {
            if (amount <= 0 || remaining <= 0 || multiplier <= 0)
                return amount;

            var boosted = amount * (1.0 + multiplier);

            if (!double.IsFinite(boosted) || boosted >= long.MaxValue)
                return amount;

            return (long)Math.Round(boosted);
        }
    }
}
