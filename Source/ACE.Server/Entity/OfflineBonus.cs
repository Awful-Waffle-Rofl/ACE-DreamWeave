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
        /// Splits an online interval [<paramref name="intervalStart"/>, <paramref name="intervalEnd"/>] into an
        /// "active" prefix (before <paramref name="activeUntil"/>, the end of the trailing idle-timeout window
        /// from the character's most recent qualifying XP/Luminance grant) and the remaining "idle" tail. The
        /// active portion drains the bank; the idle portion accrues it. <paramref name="activeUntil"/> at or
        /// before <paramref name="intervalStart"/> (including 0, meaning no qualifying grant has happened yet)
        /// yields an entirely idle interval. The two returned values are always non-negative and always sum to
        /// exactly Max(0, intervalEnd - intervalStart).
        /// </summary>
        public static (double activeSeconds, double idleSeconds) SplitOnlineInterval(double intervalStart, double intervalEnd, double activeUntil)
        {
            var elapsed = Math.Max(0, intervalEnd - intervalStart);

            var active = Math.Clamp(activeUntil - intervalStart, 0, elapsed);
            var idle = elapsed - active;

            return (active, idle);
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

        /// <summary>
        /// The player-facing chat notice (if any) that a reconcile should produce, given the active/idle and
        /// banked/empty state before and after it. See <see cref="ClassifyTransition"/>.
        /// </summary>
        public enum OfflineBonusTransition
        {
            None,
            Activated,
            Banking,
            Exhausted
        }

        /// <summary>
        /// Decides which offline-bonus chat notice, if any, a reconcile should produce from the active/idle and
        /// banked/empty state immediately before and after it:
        ///  - Activated: the idle -> active edge, but only when the bank is non-empty. Starting to fight with an
        ///    empty bank produces no notice - there's nothing "now active" about it.
        ///  - Banking: the active -> idle edge, unconditionally. Going idle genuinely starts banking again even
        ///    at a zero balance.
        ///  - Exhausted: the bank goes from banked to empty WHILE still active (i.e. combat burned through the
        ///    last of it). Never fires when the bank empties while idle - that's ordinary accrual having nothing
        ///    to accrue from, not an exhaustion event - and never fires in the same reconcile as Banking: if the
        ///    active window closes in the same reconcile the bank empties, Banking wins, since going idle is the
        ///    more informative fact for the player.
        ///  - None otherwise.
        /// Pure and free of <see cref="WorldObjects.Player"/> state, so it is unit-testable without a live
        /// Player/Session - see Player_OfflineBonus.cs's UpdateOfflineBonus for the caller, which owns all the
        /// IO (reading the bank, sending the resulting chat, updating the tracked before-state).
        /// </summary>
        public static OfflineBonusTransition ClassifyTransition(bool wasActive, bool isActive, bool wasBanked, bool isBanked)
        {
            if (wasActive != isActive)
            {
                if (isActive)
                    return isBanked ? OfflineBonusTransition.Activated : OfflineBonusTransition.None;

                return OfflineBonusTransition.Banking;
            }

            if (isActive && wasBanked && !isBanked)
                return OfflineBonusTransition.Exhausted;

            return OfflineBonusTransition.None;
        }
    }
}
