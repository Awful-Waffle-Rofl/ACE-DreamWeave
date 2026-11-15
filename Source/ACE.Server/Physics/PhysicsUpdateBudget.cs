using System.Diagnostics;

namespace ACE.Server.Physics
{
    /// <summary>
    /// Limits on how long one object's physics update may take before the world stops spending time on it.
    ///
    /// Why: one Force Bolt's UpdateObjectPhysics took 28 s and froze the world, and several more took
    /// about 10.6 s each. A spell bolt's update normally takes well under 1 ms; its impact tick (damage, procs and any
    /// child projectile that hits on world entry, all inside the same update) took 10-100 ms on the live server.
    /// </summary>
    public static class PhysicsUpdateBudget
    {
        /// <summary>
        /// PhysicsObj.update_object runs up to 20 fixed steps (HugeQuantum / MaxQuantum) to catch an object up. Past
        /// this much time in one call, it stops stepping and drops the time it still owed, as it already does for a
        /// gap over HugeQuantum, so a slow step is never repeated up to 19 more times in the same tick.
        /// </summary>
        public const double StepBudgetSeconds = 0.25;

        /// <summary>One spell projectile update this long ends the projectile at once (no waiting for repeats).</summary>
        public const double ProjectileDestroySeconds = 1.0;

        /// <summary>Spell projectile updates of 10 ms or more that end the projectile (the stock ACE rule).</summary>
        public const int ProjectileSlowHitsToDestroy = 5;

        /// <summary>True once <paramref name="startTimestamp"/> (Stopwatch.GetTimestamp) is more than the step budget ago.</summary>
        public static bool StepBudgetSpent(long startTimestamp, long nowTimestamp)
        {
            return (nowTimestamp - startTimestamp) > (long)(StepBudgetSeconds * Stopwatch.Frequency);
        }

        /// <summary>
        /// Whether a spell projectile should be ended after an update that took <paramref name="elapsedSeconds"/>,
        /// with <paramref name="slowHits"/> slow updates counted so far (this one included).
        /// </summary>
        public static bool ShouldEndSlowProjectile(double elapsedSeconds, int slowHits)
        {
            return elapsedSeconds >= ProjectileDestroySeconds || slowHits >= ProjectileSlowHitsToDestroy;
        }
    }
}
