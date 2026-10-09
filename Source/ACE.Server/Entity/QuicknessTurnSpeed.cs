using System;

namespace ACE.Server.Entity
{
    /// <summary>
    /// WaffleACE: pure composition of the server-initiated player turn speed multiplier from buffed
    /// Quickness. Kept as a pure function - no PropertyManager reads - because PropertyManager reads THROW
    /// in unit tests on a cache miss, so the math has to be testable without a live shard config table
    /// (same reason as PickupSpeed.Compute). The caller (Player.TurnToSpeed) supplies the live tunables.
    /// </summary>
    public static class QuicknessTurnSpeed
    {
        /// <summary>
        /// Returns 1 + min(buffedQuickness * perPoint, maxBonus).
        ///
        /// - perPoint: non-finite becomes 0, then clamped to [0, 1].
        /// - maxBonus: non-finite becomes 0, then clamped to [0, 9], so the result never exceeds 10x.
        /// - The result is never below 1.0: a mis-set tunable must never SLOW a player's turn.
        /// </summary>
        public static double Compute(uint buffedQuickness, double perPoint, double maxBonus)
        {
            if (!double.IsFinite(perPoint))
                perPoint = 0;
            perPoint = Math.Clamp(perPoint, 0, 1);

            if (!double.IsFinite(maxBonus))
                maxBonus = 0;
            maxBonus = Math.Clamp(maxBonus, 0, 9);

            return 1.0 + Math.Min(buffedQuickness * perPoint, maxBonus);
        }
    }
}
