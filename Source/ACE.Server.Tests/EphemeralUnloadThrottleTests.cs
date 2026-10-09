using System.Collections.Generic;
using System.Linq;

using ACE.Entity;
using ACE.Server.Realms;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Covers <see cref="EphemeralUnloadThrottle{T}"/>, the drain loop of LandblockManager.UnloadLandblocks with its
    /// per-tick cap on ephemeral unloads (dynamic_dungeons_max_unloads_per_tick). A Landblock cannot be constructed
    /// in a unit test, so the fake below stands in for one; <see cref="FakeManager.Unload"/> mirrors the real
    /// UnloadOne's contract: refuse (return false, touch nothing) when a player has arrived, otherwise tear down and
    /// DEREGISTER the instance, which is the only place the instance id is released.
    /// </summary>
    [TestClass]
    public class EphemeralUnloadThrottleTests
    {
        private sealed class FakeLandblock
        {
            public string Name;
            public bool Ephemeral;
            public uint Instance;
            public bool PlayerArrived;

            public override string ToString() => Name;
        }

        private sealed class FakeManager
        {
            // FIFO rather than the real ConcurrentBag, so the order a drain meets items in is deterministic.
            public readonly Queue<FakeLandblock> Queue = new Queue<FakeLandblock>();
            public readonly EphemeralUnloadThrottle<FakeLandblock> Throttle = new EphemeralUnloadThrottle<FakeLandblock>();
            public readonly EphemeralInstanceRegistry<FakeLandblock> Registry = new EphemeralInstanceRegistry<FakeLandblock>();
            public readonly List<FakeLandblock> Unloaded = new List<FakeLandblock>();
            public readonly List<FakeLandblock> Refused = new List<FakeLandblock>();

            public bool Unload(FakeLandblock lb)
            {
                if (lb.PlayerArrived)
                {
                    Refused.Add(lb);
                    return false;
                }

                Unloaded.Add(lb);

                if (lb.Ephemeral)
                    Registry.TryUnregister(lb.Instance);

                return true;
            }

            public int Tick(int cap, bool uncapped = false)
                => Throttle.Drain(Queue.TryDequeue, lb => lb.Ephemeral, Unload, cap, uncapped);

            public FakeLandblock AddEphemeral(string name, ushort shortId)
            {
                var lb = new FakeLandblock { Name = name, Ephemeral = true, Instance = Position.InstanceIDFromVars(0, shortId, isTemporaryRuleset: true) };
                Assert.IsTrue(Registry.TryRegister(lb.Instance, lb));
                Queue.Enqueue(lb);
                return lb;
            }

            public FakeLandblock AddWorld(string name)
            {
                var lb = new FakeLandblock { Name = name, Ephemeral = false };
                Queue.Enqueue(lb);
                return lb;
            }
        }

        [TestMethod]
        public void Cap_DefersExtraEphemeralsToLaterTicks()
        {
            var m = new FakeManager();
            for (ushort i = 1; i <= 5; i++)
                m.AddEphemeral($"e{i}", i);

            Assert.AreEqual(3, m.Tick(2), "tick 1 carries three");
            Assert.AreEqual(2, m.Unloaded.Count);
            Assert.AreEqual(3, m.Throttle.CarriedCount);

            Assert.AreEqual(1, m.Tick(2), "tick 2 carries one");
            Assert.AreEqual(4, m.Unloaded.Count);

            Assert.AreEqual(0, m.Tick(2), "tick 3 finishes");
            Assert.AreEqual(5, m.Unloaded.Count);
            Assert.AreEqual(0, m.Throttle.CarriedCount);
            Assert.AreEqual(5, m.Unloaded.Distinct().Count(), "every ephemeral unloaded exactly once");
            Assert.AreEqual(0, m.Registry.LiveCount);
        }

        [TestMethod]
        public void Carried_AreOfferedFirstOnTheNextTick_InDeferralOrder()
        {
            var m = new FakeManager();
            m.AddEphemeral("a", 1);
            m.AddEphemeral("b", 2);
            m.AddEphemeral("c", 3);

            m.Tick(1);
            var carriedFirst = m.Unloaded.ToList();
            Assert.AreEqual(1, carriedFirst.Count);

            // A newcomer arrives while two are carried; the older deferrals must go first.
            var late = m.AddEphemeral("late", 4);

            m.Tick(1);
            m.Tick(1);

            Assert.AreEqual(3, m.Unloaded.Count);
            CollectionAssert.DoesNotContain(m.Unloaded, late, "the newcomer waited behind the two older deferrals");

            m.Tick(1);
            Assert.AreSame(late, m.Unloaded.Last());
        }

        [TestMethod]
        public void NonEphemeral_AreNeverCapped_OrDeduplicated()
        {
            var m = new FakeManager();
            var worlds = Enumerable.Range(0, 10).Select(i => m.AddWorld($"w{i}")).ToList();
            m.Queue.Enqueue(worlds[0]); // a duplicate queue entry, which the old loop also processed twice
            m.AddEphemeral("e1", 1);
            m.AddEphemeral("e2", 2);
            m.AddEphemeral("e3", 3);

            m.Tick(1);

            Assert.AreEqual(11, m.Unloaded.Count(lb => !lb.Ephemeral), "all ten world landblocks, the duplicate included, in the same tick");
            Assert.AreEqual(2, m.Unloaded.Count(lb => lb == worlds[0]));
            Assert.AreEqual(1, m.Unloaded.Count(lb => lb.Ephemeral), "only the ephemerals are capped");
            Assert.AreEqual(2, m.Throttle.CarriedCount);
        }

        [TestMethod]
        public void Refusal_DoesNotSpendTheCap_AndIsNotCarried()
        {
            var m = new FakeManager();
            var occupied = m.AddEphemeral("occupied", 1);
            occupied.PlayerArrived = true;
            m.AddEphemeral("e2", 2);
            m.AddEphemeral("e3", 3);
            m.AddEphemeral("e4", 4);

            Assert.AreEqual(1, m.Tick(2), "e4 is carried: the refusal did not use up a slot");

            CollectionAssert.Contains(m.Refused, occupied);
            Assert.AreEqual(2, m.Unloaded.Count, "the refusal cost nothing, so two real unloads still fit");
            Assert.IsFalse(m.Throttle.IsCarried(occupied), "a refused copy goes back into service, exactly as before");
            Assert.AreSame(occupied, m.Registry.Get(occupied.Instance));
        }

        [TestMethod]
        public void HeartbeatRequeueOfACarriedCopy_IsUnloadedExactlyOnce()
        {
            var m = new FakeManager();
            var a = m.AddEphemeral("a", 1);
            var b = m.AddEphemeral("b", 2);
            m.Queue.Enqueue(a); // duplicate entries within one drain
            m.Queue.Enqueue(b);

            m.Tick(1);
            Assert.AreEqual(1, m.Unloaded.Count);
            Assert.AreEqual(1, m.Throttle.CarriedCount, "the duplicate was not carried twice");

            var carried = m.Throttle.IsCarried(a) ? a : b;
            Assert.IsTrue(m.Throttle.IsCarried(carried));

            // The carried copy is still loaded, so its heartbeat queues it again - twice, even.
            m.Queue.Enqueue(carried);
            m.Queue.Enqueue(carried);

            m.Tick(5);
            m.Tick(5);

            Assert.AreEqual(2, m.Unloaded.Count);
            Assert.AreEqual(1, m.Unloaded.Count(lb => lb == carried), "the carried copy reached Unload exactly once despite three queue entries");
        }

        [TestMethod]
        public void Uncapped_DrainsEverything_CarriedIncluded()
        {
            var m = new FakeManager();
            for (ushort i = 1; i <= 6; i++)
                m.AddEphemeral($"e{i}", i);

            m.Tick(1);
            Assert.AreEqual(5, m.Throttle.CarriedCount);

            m.AddEphemeral("e7", 7);
            Assert.AreEqual(0, m.Tick(1, uncapped: true));
            Assert.AreEqual(7, m.Unloaded.Count);
            Assert.AreEqual(0, m.Throttle.CarriedCount);
        }

        /// <summary>
        /// The resurrection guard. While a copy is carried it has not been torn down, so its instance id is still
        /// LIVE in the registry: GetEphemeralLandblock keeps resolving it to the same object, and the allocator can
        /// never hand the id to a new run. Made decisive by filling every other id in the realm but one - the
        /// allocator has exactly two candidates left, the carried copy's id and one free id, and must return the
        /// free one.
        /// </summary>
        [TestMethod]
        public void CarriedCopy_StaysRegistered_AndItsIdIsNeverReissued()
        {
            var m = new FakeManager();
            var first = m.AddEphemeral("first", 1);
            var second = m.AddEphemeral("second", 2);

            m.Tick(1);

            var carried = m.Throttle.IsCarried(first) ? first : second;
            Assert.IsTrue(m.Throttle.IsCarried(carried));
            Assert.AreSame(carried, m.Registry.Get(carried.Instance), "a carried copy is still live and resolvable");

            const ushort freeShortId = 0x1234;
            for (var s = 1; s <= 0xFFFD; s++)
            {
                var iid = Position.InstanceIDFromVars(0, (ushort)s, true);
                if (s == freeShortId || m.Registry.Get(iid) != null)
                    continue;
                m.Registry.TryRegister(iid, new FakeLandblock { Name = "filler", Ephemeral = true, Instance = iid });
            }

            var issued = m.Registry.RequestNewInstanceId(0);

            Assert.AreEqual(Position.InstanceIDFromVars(0, freeShortId, true), issued);
            Assert.AreNotEqual(carried.Instance, issued);
        }

        /// <summary>
        /// A carried copy is re-checked when it is finally processed, not when it was deferred: a player who
        /// arrives during the deferral is honoured by the same refusal the in-tick window always had.
        /// </summary>
        [TestMethod]
        public void CarriedCopy_ReAsksTheArrivalCheck_WhenItsTurnComes()
        {
            var m = new FakeManager();
            var first = m.AddEphemeral("first", 1);
            var second = m.AddEphemeral("second", 2);

            m.Tick(1);
            var carried = m.Throttle.IsCarried(first) ? first : second;
            Assert.IsTrue(m.Throttle.IsCarried(carried));

            carried.PlayerArrived = true;
            m.Tick(1);

            CollectionAssert.Contains(m.Refused, carried);
            CollectionAssert.DoesNotContain(m.Unloaded, carried);
            Assert.AreSame(carried, m.Registry.Get(carried.Instance));
            Assert.AreEqual(0, m.Throttle.CarriedCount);
        }

        [TestMethod]
        public void Clamp_BoundsTheTunable()
        {
            Assert.AreEqual(1, EphemeralUnloadThrottle.Clamp(0));
            Assert.AreEqual(1, EphemeralUnloadThrottle.Clamp(-5));
            Assert.AreEqual(2, EphemeralUnloadThrottle.Clamp(2));
            Assert.AreEqual(100, EphemeralUnloadThrottle.Clamp(101));
            Assert.AreEqual(100, EphemeralUnloadThrottle.Clamp(long.MaxValue));
            Assert.AreEqual(2, EphemeralUnloadThrottle.DefaultMaxUnloadsPerTick);
        }
    }
}
