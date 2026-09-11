using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using ACE.Entity;
using ACE.Server.Realms;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Covers the live-ephemeral-instance registry that LandblockManager keeps
    /// (<see cref="EphemeralInstanceRegistry{T}"/>).
    ///
    /// <para/>
    /// The prod bug these exist for: the registry used to be a plain Dictionary written under
    /// LandblockManager's landblockLock and read with NO lock by the instance-routing lookup, so a
    /// structural modification concurrent with that read could return "not found" for an instance that was
    /// live. InstanceRouting read that as "the instance is gone" and silently rerouted the player to the
    /// same coordinates in their home realm's default instance - the shared-world copy of the arena
    /// landblock, which holds no monsters and no exit portal.
    ///
    /// <para/>
    /// <see cref="Registry_ConcurrentChurn_NeverMissesALiveInstance"/> is the one that would have caught it:
    /// it hammers register/unregister from several threads while reading a disjoint set of permanently
    /// registered ids, and asserts that every one of those reads finds its entry.
    /// </summary>
    [TestClass]
    public class EphemeralInstanceRegistryTests
    {
        private sealed class FakeInstance
        {
            public uint Instance { get; set; }
        }

        [TestMethod]
        public void Register_ThenGet_ReturnsTheSameValue()
        {
            var registry = new EphemeralInstanceRegistry<FakeInstance>();
            var value = new FakeInstance { Instance = 0x80010001 };

            Assert.IsTrue(registry.TryRegister(0x80010001, value));
            Assert.AreSame(value, registry.Get(0x80010001));
            Assert.AreEqual(1, registry.LiveCount);
        }

        [TestMethod]
        public void Register_SameInstanceTwice_IsRefused()
        {
            var registry = new EphemeralInstanceRegistry<FakeInstance>();

            Assert.IsTrue(registry.TryRegister(0x80010001, new FakeInstance()));
            Assert.IsFalse(registry.TryRegister(0x80010001, new FakeInstance()),
                "a second registration of a live instance id must be refused so the caller can log it");
        }

        [TestMethod]
        public void Get_UnregisteredInstance_ReturnsNull()
        {
            var registry = new EphemeralInstanceRegistry<FakeInstance>();

            Assert.IsNull(registry.Get(0x80010001));
        }

        [TestMethod]
        public void Unregister_RemovesTheEntry()
        {
            var registry = new EphemeralInstanceRegistry<FakeInstance>();
            registry.TryRegister(0x80010001, new FakeInstance());

            Assert.IsTrue(registry.TryUnregister(0x80010001));
            Assert.IsNull(registry.Get(0x80010001));
            Assert.IsFalse(registry.TryUnregister(0x80010001), "unregistering twice must report that it was already gone");
            Assert.AreEqual(0, registry.LiveCount);
        }

        [TestMethod]
        public void RequestNewInstanceId_MarksPending_UntilCleared()
        {
            var registry = new EphemeralInstanceRegistry<FakeInstance>();

            var id = registry.RequestNewInstanceId(homeRealmId: 1);

            Assert.AreEqual(1, registry.PendingCount);

            registry.ClearPending(id);

            Assert.AreEqual(0, registry.PendingCount);

            // idempotent, and harmless for an id that was never pending
            registry.ClearPending(id);
            registry.ClearPending(0xDEADBEEF);
            Assert.AreEqual(0, registry.PendingCount);
        }

        [TestMethod]
        public void RequestNewInstanceId_EncodesEphemeralBitAndRealm()
        {
            var registry = new EphemeralInstanceRegistry<FakeInstance>();

            for (var i = 0; i < 200; i++)
            {
                var id = registry.RequestNewInstanceId(homeRealmId: 42);

                Position.ParseInstanceID(id, out var isEphemeral, out var realmId, out var shortInstanceId);

                Assert.IsTrue(isEphemeral, $"0x{id:X8} is missing the ephemeral bit");
                Assert.AreEqual((ushort)42, realmId);
                Assert.IsTrue(shortInstanceId >= 1 && shortInstanceId <= 0xFFFD, $"short id {shortInstanceId} out of range");
            }
        }

        /// <summary>
        /// The regression test for the prod bug. Two writer threads continuously register and unregister a
        /// large churn set (enough entries to force the backing store to resize repeatedly) while two reader
        /// threads look up a DISJOINT set of ids that are registered once at the start and never removed.
        /// Every one of those reads must find its entry: a null there means a read observed a torn or
        /// mid-resize store, which is exactly the failure that dropped a player into an empty arena.
        /// </summary>
        [TestMethod]
        public void Registry_ConcurrentChurn_NeverMissesALiveInstance()
        {
            var registry = new EphemeralInstanceRegistry<FakeInstance>();

            // permanently live, never unregistered - reads of these must ALWAYS succeed
            const int stableCount = 64;
            var stable = new uint[stableCount];

            for (var i = 0; i < stableCount; i++)
            {
                stable[i] = 0x80000000u | (uint)(i + 1);
                Assert.IsTrue(registry.TryRegister(stable[i], new FakeInstance { Instance = stable[i] }));
            }

            // disjoint id space for the churn, so a churn write can never collide with a stable id
            const int churnCount = 512;

            var misses = new ConcurrentBag<string>();
            var reads = 0L;
            var writes = 0L;

            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
            {
                var token = cts.Token;

                Action writer = () =>
                {
                    var rng = new Random(Thread.CurrentThread.ManagedThreadId);

                    while (!token.IsCancellationRequested)
                    {
                        var id = 0x81000000u | (uint)rng.Next(1, churnCount + 1);

                        if (registry.TryRegister(id, new FakeInstance { Instance = id }))
                            registry.TryUnregister(id);

                        Interlocked.Increment(ref writes);
                    }
                };

                Action reader = () =>
                {
                    while (!token.IsCancellationRequested)
                    {
                        for (var i = 0; i < stableCount; i++)
                        {
                            var found = registry.Get(stable[i]);

                            if (found == null)
                                misses.Add($"0x{stable[i]:X8} read as unregistered while live");
                            else if (found.Instance != stable[i])
                                misses.Add($"0x{stable[i]:X8} returned the value for 0x{found.Instance:X8}");

                            Interlocked.Increment(ref reads);
                        }
                    }
                };

                Task.WaitAll(
                    Task.Run(writer), Task.Run(writer),
                    Task.Run(reader), Task.Run(reader));
            }

            Assert.AreEqual(0, misses.Count,
                $"missed lookups against a live instance during concurrent churn: {string.Join("; ", misses)}");

            // guard against the test passing because it never actually ran anything
            Assert.IsTrue(Interlocked.Read(ref reads) > 10_000, $"only {reads} reads were performed - the test did not exercise the registry");
            Assert.IsTrue(Interlocked.Read(ref writes) > 1_000, $"only {writes} writes were performed - the test did not exercise the registry");

            // and the stable set survived the whole run
            for (var i = 0; i < stableCount; i++)
                Assert.IsNotNull(registry.Get(stable[i]));
        }

        /// <summary>
        /// Concurrent allocation must never hand the same instance id to two callers, even before either
        /// one's landblock exists - that is what the pending set is for.
        /// </summary>
        [TestMethod]
        public void RequestNewInstanceId_ConcurrentAllocation_NeverCollides()
        {
            var registry = new EphemeralInstanceRegistry<FakeInstance>();
            var allocated = new ConcurrentBag<uint>();

            const int perThread = 500;
            const int threads = 4;

            Parallel.For(0, threads, _ =>
            {
                for (var i = 0; i < perThread; i++)
                    allocated.Add(registry.RequestNewInstanceId(homeRealmId: 7));
            });

            var seen = new HashSet<uint>();

            foreach (var id in allocated)
                Assert.IsTrue(seen.Add(id), $"instance id 0x{id:X8} was allocated twice");

            Assert.AreEqual(threads * perThread, seen.Count);
            Assert.AreEqual(threads * perThread, registry.PendingCount);
        }
    }
}
