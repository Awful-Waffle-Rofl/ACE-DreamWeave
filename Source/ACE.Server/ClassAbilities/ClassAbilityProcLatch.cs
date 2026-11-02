using System;

namespace ACE.Server.ClassAbilities
{
    /// <summary>
    /// The arm / run / always-disarm shape behind Player's per-cast proc latches, as a dat-free static so the
    /// reset guarantee is unit-testable without a live Player (ACE.Server.Tests cannot construct one: both
    /// Player constructors reach the auth database). Player.CastClassAbilityProc(cast, scale,
    /// rendSourceDamageType) routes its RendSourceDamageType latch through here.
    /// </summary>
    public static class ClassAbilityProcLatch
    {
        /// <summary>
        /// Sets the latch to <paramref name="armed"/>, runs <paramref name="body"/>, and sets it to
        /// <paramref name="disarmed"/> in a finally - so a body that throws cannot leave a later, unrelated cast
        /// stamped. The exception still propagates.
        /// </summary>
        public static void Run<T>(Action<T> set, T armed, T disarmed, Action body)
        {
            set(armed);

            try
            {
                body?.Invoke();
            }
            finally
            {
                set(disarmed);
            }
        }
    }
}
