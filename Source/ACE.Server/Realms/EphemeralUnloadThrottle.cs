using System;
using System.Collections.Generic;

namespace ACE.Server.Realms
{
    /// <summary>Tunable bounds for <see cref="EphemeralUnloadThrottle{T}"/> (dynamic_dungeons_max_unloads_per_tick).</summary>
    public static class EphemeralUnloadThrottle
    {
        /// <summary>The shipped default of dynamic_dungeons_max_unloads_per_tick.</summary>
        public const int DefaultMaxUnloadsPerTick = 2;

        /// <summary>dynamic_dungeons_max_unloads_per_tick is clamped to [<see cref="MinMaxUnloadsPerTick"/>, <see cref="MaxMaxUnloadsPerTick"/>] at read.</summary>
        public const int MinMaxUnloadsPerTick = 1;
        public const int MaxMaxUnloadsPerTick = 100;

        /// <summary>Clamps a configured dynamic_dungeons_max_unloads_per_tick to [1, 100].</summary>
        public static int Clamp(long configured) => (int)Math.Clamp(configured, MinMaxUnloadsPerTick, MaxMaxUnloadsPerTick);
    }

    /// <summary>
    /// The drain loop of LandblockManager.UnloadLandblocks, with a per-tick cap on EPHEMERAL unloads.
    ///
    /// <para/>
    /// Why. UnloadLandblocks runs on the world thread at the end of LandblockManager.Tick and used to drain the whole
    /// destruction queue in one tick. A Thread reap pass (every 15 s) can end several runs at once, and each ephemeral
    /// copy's teardown (Landblock.Unload destroying every object, plus deregistration) is paid serially on that one
    /// thread, so the tick that received them all stalled for their sum. This spreads ephemeral teardown across ticks.
    ///
    /// <para/>
    /// What is capped, and what is not.
    ///   - A NON-ephemeral item is handed to the unload delegate exactly as the old loop did: every one taken, in the
    ///     order the queue yields it, no dedup, no cap. The ordinary world is untouched.
    ///   - An ephemeral item counts against the cap only when the delegate reports it actually unloaded. A refusal
    ///     (Landblock.TryClaimForDestruction: a player arrived since it was queued) is cheap, costs nothing, and puts
    ///     the landblock back into service exactly as before - it is NOT carried to the next tick.
    ///   - Ephemeral items past the cap are carried in a FIFO owned by this object and offered FIRST on the next
    ///     drain, so a steady trickle of new ends cannot starve an older one.
    ///   - Ephemeral items are deduplicated within a drain and across the carry. A deferred copy stays loaded and keeps
    ///     heartbeating, so its heartbeat re-queues it every few seconds; without the dedup the second copy of the
    ///     entry would reach Landblock.Unload after the first had already torn the landblock down.
    ///   - <c>uncapped</c> (shutdown) processes everything, carry included, so ServerManager's wait-for-empty loop is
    ///     never slowed.
    ///
    /// <para/>
    /// Why a deferred copy cannot be resurrected wrongly. Deferral happens BEFORE the unload delegate runs, and the
    /// delegate is the only thing that deregisters the instance (EphemeralInstanceRegistry.TryUnregister, inside
    /// LandblockManager's write lock). So while an item is carried, its instance id stays LIVE in the registry, and
    /// EphemeralInstanceRegistry.RequestNewInstanceId refuses any live id - the id cannot be handed to a new run. And
    /// because the deferred item goes through the SAME delegate later, the arrival check
    /// (Landblock.TryClaimForDestruction) is re-asked at the moment it is actually torn down, not at the moment it was
    /// deferred. See EphemeralUnloadThrottleTests.
    ///
    /// <para/>
    /// Threading. Not thread safe, and does not need to be: one instance, owned by LandblockManager, and only
    /// UnloadLandblocks calls it, from the world thread. The shared queue itself stays the concurrent one it was.
    /// Generic purely so the contract can be unit tested; a Landblock cannot be constructed in a test.
    /// </summary>
    internal sealed class EphemeralUnloadThrottle<T> where T : class
    {
        private readonly Queue<T> carried = new Queue<T>();
        private readonly HashSet<T> carriedSet = new HashSet<T>(ReferenceEqualityComparer.Instance);

        /// <summary>Ephemeral items waiting for a later tick. For diagnostics and tests.</summary>
        public int CarriedCount => carried.Count;

        /// <summary>True when <paramref name="item"/> is waiting for a later tick. For tests.</summary>
        internal bool IsCarried(T item) => carriedSet.Contains(item);

        /// <summary>
        /// One tick's drain. <paramref name="tryTake"/> pulls from the shared destruction queue until it is empty;
        /// <paramref name="unload"/> is the per-item body and returns true when it did the teardown, false when the
        /// item was refused. Returns the number of ephemeral items this drain carried to the next.
        /// </summary>
        public int Drain(TryTake tryTake, Func<T, bool> isEphemeral, Func<T, bool> unload, int maxEphemeralUnloads, bool uncapped)
        {
            var remaining = uncapped ? int.MaxValue : Math.Max(1, maxEphemeralUnloads);
            var seen = new HashSet<T>(ReferenceEqualityComparer.Instance);
            List<T> carry = null;

            void OfferEphemeral(T item)
            {
                if (!seen.Add(item))
                    return;

                if (remaining > 0)
                {
                    if (unload(item))
                        remaining--;
                }
                else
                {
                    (carry ??= new List<T>()).Add(item);
                }
            }

            // Older deferrals first, in the order they were deferred.
            while (carried.Count > 0)
            {
                var item = carried.Dequeue();
                carriedSet.Remove(item);
                OfferEphemeral(item);
            }

            while (tryTake(out var item))
            {
                if (!isEphemeral(item))
                {
                    unload(item);
                    continue;
                }

                OfferEphemeral(item);
            }

            if (carry == null)
                return 0;

            foreach (var item in carry)
            {
                if (carriedSet.Add(item))
                    carried.Enqueue(item);
            }

            return carry.Count;
        }

        /// <summary>The shape of ConcurrentBag.TryTake, so the real queue can be passed as a method group.</summary>
        public delegate bool TryTake(out T item);
    }
}
