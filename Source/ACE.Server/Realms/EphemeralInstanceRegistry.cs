using System;
using System.Collections.Concurrent;

namespace ACE.Server.Realms
{
    /// <summary>
    /// The live-ephemeral-instance registry that backs <see cref="ACE.Server.Managers.LandblockManager"/>:
    /// which ephemeral instance ids currently have a loaded landblock, plus the ids that have been handed
    /// out by an allocator but whose landblock has not been registered yet ("pending").
    ///
    /// <para/>
    /// It exists as its own type for two reasons.
    ///
    /// <para/>
    /// FIRST, THREAD SAFETY. This used to be a plain <c>Dictionary</c> plus a plain <c>HashSet</c> inside
    /// LandblockManager, written under LandblockManager's <c>landblockLock</c> write lock (landblock create
    /// and unload) but read under a DIFFERENT lock for id allocation, and under NO lock at all by the
    /// instance-routing lookup. A structural modification concurrent with that lock-free
    /// <c>TryGetValue</c> can return false for a key that is present, and the caller
    /// (<see cref="InstanceRouting.ValidateInstanceDestination"/>) treated "not found" as "this instance is
    /// gone" and silently rerouted the player to the same coordinates in their home realm's default
    /// instance - a shared-world copy of the landblock, which for the Proving Grounds arenas is empty
    /// geometry with no monsters and no exit portal. Both collections are concurrent here, so every read is
    /// safe with no lock and no lock-ordering question against <c>landblockLock</c>: the only lock this type
    /// takes is a private one, held exclusively inside <see cref="RequestNewInstanceId"/> and never while
    /// calling out.
    ///
    /// <para/>
    /// SECOND, TESTABILITY. LandblockManager cannot be exercised from a unit test - a
    /// <c>Landblock</c> cannot even be constructed without the client dat files and a world database - so
    /// the registry is generic in its value type purely so the concurrency contract can be hammered
    /// directly. See <c>EphemeralInstanceRegistryTests</c>. Internal, and visible to ACE.Server.Tests via
    /// InternalsVisibleTo in ACE.Server.csproj.
    /// </summary>
    /// <typeparam name="T">The value stored per live instance - <c>Landblock</c> in the server.</typeparam>
    internal sealed class EphemeralInstanceRegistry<T> where T : class
    {
        /// <summary>Live ephemeral instances, keyed by their full 32-bit instance id.</summary>
        private readonly ConcurrentDictionary<uint, T> live = new ConcurrentDictionary<uint, T>();

        /// <summary>
        /// Ids handed out by <see cref="RequestNewInstanceId"/> whose landblock has not been registered yet.
        /// Used as a set; the byte value is unused. A ConcurrentDictionary rather than a locked HashSet
        /// specifically so <see cref="ClearPending"/> is lock-free: it runs on every landblock load, which
        /// is on the physics path.
        /// </summary>
        private readonly ConcurrentDictionary<uint, byte> pending = new ConcurrentDictionary<uint, byte>();

        /// <summary>
        /// Guards <see cref="random"/> only. <see cref="Random"/> instances are not thread safe, and this
        /// allocator is called from portal use, gem use and login, all of which can be on different threads.
        /// Nothing else takes this lock, and no other lock is ever taken while holding it.
        /// </summary>
        private readonly object allocationLock = new object();
        private readonly Random random = new Random();

        /// <summary>Number of live registered instances. Snapshot; for diagnostics and tests.</summary>
        public int LiveCount => live.Count;

        /// <summary>Number of allocated-but-not-yet-registered instance ids. Snapshot; for diagnostics and tests.</summary>
        public int PendingCount => pending.Count;

        /// <summary>
        /// The value registered for a live ephemeral instance, or null if no landblock is loaded for it.
        /// Lock free and always coherent: a null result means the instance genuinely was not registered at
        /// the moment of the read, never a torn or mid-resize dictionary.
        /// </summary>
        public T Get(uint instance)
        {
            live.TryGetValue(instance, out var value);
            return value;
        }

        /// <summary>
        /// Registers a live instance. False if the id was already registered, which is a bug in the caller
        /// (instance ids are allocated unique and unregistered on unload) and should be logged.
        /// </summary>
        public bool TryRegister(uint instance, T value)
        {
            return live.TryAdd(instance, value);
        }

        /// <summary>Removes a live instance. False if it was not registered.</summary>
        public bool TryUnregister(uint instance)
        {
            return live.TryRemove(instance, out _);
        }

        /// <summary>
        /// Releases an allocated id's "pending" reservation, called once its landblock has been registered
        /// (or once the caller has given up on it). Idempotent, and a no-op for an id that was never pending.
        /// </summary>
        public void ClearPending(uint instance)
        {
            pending.TryRemove(instance, out _);
        }

        /// <summary>
        /// Allocates a fresh ephemeral instance id for a realm: the ephemeral bit set, the realm in the
        /// middle 15 bits, and a random 16-bit short id, guaranteed not to collide with a live or pending
        /// ephemeral instance. The id is marked pending until <see cref="ClearPending"/> is called for it,
        /// so two concurrent allocations can never hand out the same id even before either landblock exists.
        /// </summary>
        public uint RequestNewInstanceId(ushort homeRealmId)
        {
            uint iid;

            lock (allocationLock)
            {
                do
                {
                    var shortInstanceId = (ushort)random.Next(1, 0xFFFE);
                    iid = ACE.Entity.Position.InstanceIDFromVars(homeRealmId, shortInstanceId, isTemporaryRuleset: true);
                }
                while (live.ContainsKey(iid) || !pending.TryAdd(iid, 0));
            }

            return iid;
        }
    }
}
