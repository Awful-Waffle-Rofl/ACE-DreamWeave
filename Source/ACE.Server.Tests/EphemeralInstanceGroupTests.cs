using System.Linq;

using ACE.Entity;
using ACE.Server.Realms;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Open World Threads P0 (instance groups): the pure decisions in <see cref="EphemeralInstanceGroup"/> and
    /// <see cref="EphemeralFootprint"/>, plus the footprint half of <see cref="EphemeralInstanceRegistry{T}"/>.
    /// A Landblock cannot be constructed in this harness (its constructor reads the client dat), so every rule a
    /// landblock under an ephemeral id obeys is a pure function of (landblock id, instance, registry state), and is
    /// tested as one here. EphemeralInstanceGroupWiringTests pins that production calls them.
    /// No PropertyManager keys are read by anything under test.
    /// </summary>
    [TestClass]
    public class EphemeralInstanceGroupTests
    {
        // Holtburg-ish outdoor block 0xA9B4 as the primary of a 3x3 footprint: members 0xA8B3..0xAAB5.
        private const ushort Primary = 0xA9B4;
        private const ushort Member = 0xAAB5;      // NE corner of the footprint
        private const ushort Buffer = 0xABB6;      // diagonal neighbour of the NE corner, outside the footprint
        private const ushort BufferEdge = 0xA9B6;  // north of the north edge
        private const ushort Unrelated = 0xADB4;   // four blocks east of the primary

        private static readonly uint Ephemeral = Position.InstanceIDFromVars(0, 0x1234, true);
        private static readonly uint OtherEphemeral = Position.InstanceIDFromVars(0, 0x4321, true);
        private static readonly uint BaseWorld = Position.InstanceIDFromVars(0, 0, false);
        private static readonly uint Realm1 = Position.InstanceIDFromVars(1, 0, false);

        private static EphemeralFootprint ThreeByThree() => EphemeralFootprint.Square(Primary, 1);

        // ------------------------------------------------------------------ footprint shape

        [TestMethod]
        public void Square_Radius1_IsNineLandblocksWithThePrimary()
        {
            var fp = ThreeByThree();

            Assert.AreEqual(Primary, fp.Primary);
            Assert.AreEqual(9, fp.Count);
            Assert.IsTrue(fp.Contains(0xA8B3) && fp.Contains(0xAAB5) && fp.Contains(Primary));
            Assert.IsFalse(fp.Contains(0xABB4), "two blocks east is outside a radius-1 footprint");
        }

        [TestMethod]
        public void Square_Radius0_IsSingle_AndSingleIsThePrimaryAlone()
        {
            Assert.IsTrue(EphemeralFootprint.Square(Primary, 0).IsSingle);

            var single = EphemeralFootprint.Single(Primary);
            Assert.IsTrue(single.IsSingle);
            Assert.AreEqual(1, single.Count);
            Assert.IsTrue(single.Contains(Primary));
        }

        [TestMethod]
        public void Square_AtTheMapEdge_IsClippedNotWrapped()
        {
            var fp = EphemeralFootprint.Square(0x0000, 1);

            Assert.AreEqual(4, fp.Count, "0,0 with radius 1 keeps only 0..1 on each axis");
            Assert.IsFalse(fp.Landblocks.Any(lb => (lb >> 8) > 1 || (lb & 0xFF) > 1), "no wrapped coordinates");
        }

        [TestMethod]
        public void IsBuffer_IsTheOneRingOutsideTheFootprint()
        {
            var fp = ThreeByThree();

            Assert.IsTrue(fp.IsBuffer(Buffer), "diagonal of a corner member is buffer");
            Assert.IsTrue(fp.IsBuffer(BufferEdge), "north of the north edge is buffer");
            Assert.IsFalse(fp.IsBuffer(Member), "a member is never buffer");
            Assert.IsFalse(fp.IsBuffer(Primary), "the primary is never buffer");
            Assert.IsFalse(fp.IsBuffer(Unrelated), "two rings out is not buffer");
        }

        // ------------------------------------------------------------------ realm to inherit (requirement 2)

        [TestMethod]
        public void Classify_FootprintMember_IsMember_AndInheritsRealmWithContent()
        {
            var role = EphemeralInstanceGroup.Classify(Member, Ephemeral, ThreeByThree(), registersInstance: false);

            Assert.AreEqual(EphemeralLoadRole.Member, role);
            Assert.IsTrue(EphemeralInstanceGroup.InheritsRealm(role));
            Assert.IsTrue(EphemeralInstanceGroup.LoadsWorldContent(role));
            Assert.IsTrue(EphemeralInstanceGroup.LoadsAdjacents(role));
        }

        [TestMethod]
        public void Classify_AdjacentOutsideFootprint_IsBuffer_BareWithNoRealmAndNoFurtherAdjacents()
        {
            foreach (var lb in new[] { Buffer, BufferEdge })
            {
                var role = EphemeralInstanceGroup.Classify(lb, Ephemeral, ThreeByThree(), registersInstance: false);

                Assert.AreEqual(EphemeralLoadRole.Buffer, role, $"0x{lb:X4}");
                Assert.IsFalse(EphemeralInstanceGroup.InheritsRealm(role), "a buffer must not carry the run's realm");
                Assert.IsFalse(EphemeralInstanceGroup.LoadsWorldContent(role), "a buffer must load no retail content");
                Assert.IsFalse(EphemeralInstanceGroup.LoadsAdjacents(role), "a buffer must not walk adjacency loading further out");
            }
        }

        [TestMethod]
        public void Classify_FarOutsideFootprint_IsUnrelated_AndBare()
        {
            var role = EphemeralInstanceGroup.Classify(Unrelated, Ephemeral, ThreeByThree(), registersInstance: false);

            Assert.AreEqual(EphemeralLoadRole.Unrelated, role);
            Assert.IsFalse(EphemeralInstanceGroup.InheritsRealm(role));
            Assert.IsFalse(EphemeralInstanceGroup.LoadsWorldContent(role));
            Assert.IsFalse(EphemeralInstanceGroup.LoadsAdjacents(role));
        }

        [TestMethod]
        public void Classify_EphemeralIdWithNoLiveRegistration_IsOrphan_AndBare()
        {
            // The pre-P0 leak: a landblock under a dead or never-registered ephemeral id loaded full base content.
            var role = EphemeralInstanceGroup.Classify(Member, Ephemeral, null, registersInstance: false);

            Assert.AreEqual(EphemeralLoadRole.Orphan, role);
            Assert.IsFalse(EphemeralInstanceGroup.InheritsRealm(role));
            Assert.IsFalse(EphemeralInstanceGroup.LoadsWorldContent(role));
        }

        [TestMethod]
        public void Classify_SingleLandblockInstance_EveryNeighbourIsBuffer()
        {
            // An indoor Thread has a single footprint; were it ever outdoors, its neighbours must still be bare.
            var single = EphemeralFootprint.Single(Primary);

            Assert.AreEqual(EphemeralLoadRole.Buffer, EphemeralInstanceGroup.Classify(0xAAB5, Ephemeral, single, false));
            Assert.AreEqual(EphemeralLoadRole.Buffer, EphemeralInstanceGroup.Classify(0xA8B3, Ephemeral, single, false));
        }

        [TestMethod]
        public void Classify_RegisteringLoad_IsPrimary_AndAReloadOfTheLivePrimaryIsOrphan()
        {
            Assert.AreEqual(EphemeralLoadRole.Primary, EphemeralInstanceGroup.Classify(Primary, Ephemeral, null, registersInstance: true));
            Assert.AreEqual(EphemeralLoadRole.Orphan, EphemeralInstanceGroup.Classify(Primary, Ephemeral, ThreeByThree(), registersInstance: false),
                "a non-registering load of a live instance's primary means registry and landblock table disagree; it must not claim the instance");
        }

        [TestMethod]
        public void Classify_PersistentInstances_AreNotEphemeral_AndUnchanged()
        {
            foreach (var instance in new[] { BaseWorld, Realm1 })
            {
                var role = EphemeralInstanceGroup.Classify(Member, instance, ThreeByThree(), false);

                Assert.AreEqual(EphemeralLoadRole.NotEphemeral, role);
                Assert.IsFalse(EphemeralInstanceGroup.InheritsRealm(role));
                Assert.IsTrue(EphemeralInstanceGroup.LoadsWorldContent(role), "the real world keeps its content");
                Assert.IsTrue(EphemeralInstanceGroup.LoadsAdjacents(role), "the real world keeps loading adjacents");
            }
        }

        // ------------------------------------------------------------------ may unregister (requirement 3)

        [TestMethod]
        public void MayUnregister_OnlyThePrimary()
        {
            var fp = ThreeByThree();

            Assert.IsTrue(EphemeralInstanceGroup.MayUnregister(Primary, fp));
            Assert.IsFalse(EphemeralInstanceGroup.MayUnregister(Member, fp), "a member's unload must not end the whole run");
            Assert.IsFalse(EphemeralInstanceGroup.MayUnregister(Buffer, fp), "a buffer's unload must not end the run");
            Assert.IsFalse(EphemeralInstanceGroup.MayUnregister(Primary, null), "nothing to unregister when the instance is not live");
        }

        [TestMethod]
        public void MayUnregister_SingleInstance_PrimaryStillUnregisters()
        {
            Assert.IsTrue(EphemeralInstanceGroup.MayUnregister(Primary, EphemeralFootprint.Single(Primary)), "today's dungeon copies must still unregister on unload");
        }

        [TestMethod]
        public void DecidePrimaryUnload_HoldsWhileOccupied_DrainsTheGroupFirst_UnloadsLast()
        {
            Assert.AreEqual(EphemeralInstanceGroup.PrimaryUnloadDecision.Unload, EphemeralInstanceGroup.DecidePrimaryUnload(0, 0),
                "a single-landblock instance has nothing else loaded and unloads as before");
            Assert.AreEqual(EphemeralInstanceGroup.PrimaryUnloadDecision.HoldOccupied, EphemeralInstanceGroup.DecidePrimaryUnload(4, 1),
                "a player on a member keeps the primary loaded");
            Assert.AreEqual(EphemeralInstanceGroup.PrimaryUnloadDecision.DrainGroupFirst, EphemeralInstanceGroup.DecidePrimaryUnload(4, 0),
                "an empty group unloads its other landblocks before the primary, so the id cannot be reallocated under them");
        }

        // ------------------------------------------------------------------ routing (requirement 4)

        [TestMethod]
        public void RoutingAccepts_AnyCellOfAnyFootprintLandblock()
        {
            var fp = ThreeByThree();

            Assert.IsTrue(EphemeralInstanceGroup.RoutingAccepts(fp, ((uint)Primary << 16) | 0x0001));
            Assert.IsTrue(EphemeralInstanceGroup.RoutingAccepts(fp, ((uint)Member << 16) | 0x0040), "outdoor cell of a member");
            Assert.IsTrue(EphemeralInstanceGroup.RoutingAccepts(fp, ((uint)Member << 16) | 0x0105), "indoor cell of a member");
        }

        [TestMethod]
        public void RoutingRejects_BufferUnrelatedAndNotLive()
        {
            var fp = ThreeByThree();

            Assert.IsFalse(EphemeralInstanceGroup.RoutingAccepts(fp, ((uint)Buffer << 16) | 0x0001), "a buffer is never a destination");
            Assert.IsFalse(EphemeralInstanceGroup.RoutingAccepts(fp, ((uint)Unrelated << 16) | 0x0001));
            Assert.IsFalse(EphemeralInstanceGroup.RoutingAccepts(null, ((uint)Primary << 16) | 0x0001));
        }

        [TestMethod]
        public void RoutingAccepts_SingleFootprint_IsExactlyTheOldSameLandblockRule()
        {
            var single = EphemeralFootprint.Single(Primary);

            Assert.IsTrue(EphemeralInstanceGroup.RoutingAccepts(single, ((uint)Primary << 16) | 0x0123));
            Assert.IsFalse(EphemeralInstanceGroup.RoutingAccepts(single, ((uint)Member << 16) | 0x0001), "a dungeon copy still rejects every other landblock");
        }

        // ------------------------------------------------------------------ tick grouping (requirement 5)

        [TestMethod]
        public void TickGroupKey_SeparatesAnEphemeralCopyFromTheRealWorld_AndFromOtherCopies()
        {
            Assert.IsFalse(EphemeralInstanceGroup.MayShareTickGroup(Ephemeral, BaseWorld), "an outdoor copy must not merge into the base world's tick group");
            Assert.IsFalse(EphemeralInstanceGroup.MayShareTickGroup(Ephemeral, OtherEphemeral), "two copies must not merge");
            Assert.IsTrue(EphemeralInstanceGroup.MayShareTickGroup(Ephemeral, Ephemeral), "one copy's own landblocks must group together");
        }

        [TestMethod]
        public void TickGroupKey_PersistentRealmsStillShareAKey()
        {
            Assert.IsTrue(EphemeralInstanceGroup.MayShareTickGroup(BaseWorld, Realm1), "P0 deliberately leaves persistent-realm clustering as it was");
            Assert.AreEqual(0u, EphemeralInstanceGroup.TickGroupKey(BaseWorld));
            Assert.AreEqual(Ephemeral, EphemeralInstanceGroup.TickGroupKey(Ephemeral));
        }

        // ------------------------------------------------------------------ registry footprint (requirement 1)

        private sealed class FakeLandblock
        {
        }

        [TestMethod]
        public void Registry_FootprintIsQueryableByInstanceId()
        {
            var registry = new EphemeralInstanceRegistry<FakeLandblock>();
            var primary = new FakeLandblock();
            var fp = ThreeByThree();

            Assert.IsTrue(registry.TryRegister(Ephemeral, primary, fp));
            Assert.AreSame(fp, registry.GetFootprint(Ephemeral));
            Assert.IsTrue(registry.TryGet(Ephemeral, out var value, out var footprint));
            Assert.AreSame(primary, value);
            Assert.AreSame(fp, footprint);

            Assert.IsNull(registry.GetFootprint(OtherEphemeral));
            Assert.IsFalse(registry.TryGet(OtherEphemeral, out value, out footprint));
            Assert.IsNull(value);
            Assert.IsNull(footprint);
        }

        [TestMethod]
        public void Registry_ValueMatchedUnregister_RefusesAStranger()
        {
            var registry = new EphemeralInstanceRegistry<FakeLandblock>();
            var primary = new FakeLandblock();

            registry.TryRegister(Ephemeral, primary, ThreeByThree());

            Assert.IsFalse(registry.TryUnregister(Ephemeral, new FakeLandblock()), "a different landblock under the same id must not unregister it");
            Assert.AreSame(primary, registry.Get(Ephemeral));

            Assert.IsTrue(registry.TryUnregister(Ephemeral, primary));
            Assert.IsNull(registry.Get(Ephemeral));
            Assert.IsNull(registry.GetFootprint(Ephemeral), "the footprint goes with the registration");
            Assert.IsFalse(registry.TryUnregister(Ephemeral, primary), "second unregister is a no-op");
        }

        // ------------------------------------------------------------------ allocator vs still-loaded ids (review F1)

        [TestMethod]
        public void Allocator_NeverReturnsAnIdSomethingIsStillLoadedUnder()
        {
            var registry = new EphemeralInstanceRegistry<FakeLandblock>();

            // Every short id in realm 0 is "still loaded" except one, so the only legal answer is that one. Without the
            // predicate the allocator returns a random id, which is that one with probability about 1 in 65533.
            var onlyFree = Position.InstanceIDFromVars(0, 0x2222, true);
            var consulted = 0;

            var iid = registry.RequestNewInstanceId(0, id => { consulted++; return id != onlyFree; });

            Assert.AreEqual(onlyFree, iid);
            Assert.IsTrue(consulted > 0, "the predicate must be consulted");
        }

        [TestMethod]
        public void Allocator_SkipsLoadedIds_AcrossManyDraws()
        {
            var registry = new EphemeralInstanceRegistry<FakeLandblock>();

            // Odd short ids are "still loaded". 200 draws without the predicate would hit one almost surely.
            System.Func<uint, bool> loaded = id => (id & 1) == 1;

            for (var i = 0; i < 200; i++)
            {
                var iid = registry.RequestNewInstanceId(0, loaded);
                Assert.IsFalse(loaded(iid), $"draw {i} returned 0x{iid:X8}, which a landblock is still loaded under");
                registry.ClearPending(iid);
            }
        }

        // ------------------------------------------------------------------ content-source chooser (review F3)

        [TestMethod]
        public void ContentSources_FootprintAndPersistent_LoadEverySource()
        {
            foreach (var role in new[] { EphemeralLoadRole.Primary, EphemeralLoadRole.Member, EphemeralLoadRole.NotEphemeral })
            {
                var sources = EphemeralInstanceGroup.ContentSourcesFor(role);

                Assert.AreEqual(EphemeralContentSources.All, sources, role.ToString());
                Assert.IsTrue(sources.HasFlag(EphemeralContentSources.WorldRows) && sources.HasFlag(EphemeralContentSources.ShardStatics)
                    && sources.HasFlag(EphemeralContentSources.ShardDynamics) && sources.HasFlag(EphemeralContentSources.Encounters), role.ToString());
            }
        }

        [TestMethod]
        public void ContentSources_BufferUnrelatedOrphan_LoadNothing()
        {
            foreach (var role in new[] { EphemeralLoadRole.Buffer, EphemeralLoadRole.Unrelated, EphemeralLoadRole.Orphan })
                Assert.AreEqual(EphemeralContentSources.None, EphemeralInstanceGroup.ContentSourcesFor(role), role.ToString());
        }

        [TestMethod]
        public void TimesUnload_OnlyThePrimary()
        {
            Assert.IsTrue(EphemeralInstanceGroup.TimesUnload(EphemeralLoadRole.Primary));

            foreach (var role in new[] { EphemeralLoadRole.Member, EphemeralLoadRole.Buffer, EphemeralLoadRole.Unrelated, EphemeralLoadRole.Orphan, EphemeralLoadRole.NotEphemeral })
                Assert.IsFalse(EphemeralInstanceGroup.TimesUnload(role), role.ToString());
        }

        // ------------------------------------------------------------------ group scan for the primary decision (review F3)

        private sealed class FakeLoaded
        {
            public uint Instance;
            public bool Occupied;
        }

        [TestMethod]
        public void SelectGroupOthers_SameInstanceOnly_ExcludesThePrimary_CountsOccupied()
        {
            var primary = new FakeLoaded { Instance = Ephemeral };
            var memberEmpty = new FakeLoaded { Instance = Ephemeral };
            var memberOccupied = new FakeLoaded { Instance = Ephemeral, Occupied = true };
            var otherCopy = new FakeLoaded { Instance = OtherEphemeral, Occupied = true };
            var realWorld = new FakeLoaded { Instance = BaseWorld, Occupied = true };

            var others = EphemeralInstanceGroup.SelectGroupOthers(new[] { realWorld, primary, memberEmpty, otherCopy, memberOccupied },
                primary, f => f.Instance, f => f.Occupied, out var occupied);

            CollectionAssert.AreEquivalent(new[] { memberEmpty, memberOccupied }, others);
            Assert.AreEqual(1, occupied, "only the occupied member of THIS instance counts");
            Assert.AreEqual(EphemeralInstanceGroup.PrimaryUnloadDecision.HoldOccupied, EphemeralInstanceGroup.DecidePrimaryUnload(others.Count, occupied));
        }

        [TestMethod]
        public void SelectGroupOthers_PrimaryAlone_UnloadsAsBefore()
        {
            var primary = new FakeLoaded { Instance = Ephemeral, Occupied = true };
            var lookalike = new FakeLoaded { Instance = Ephemeral };

            var alone = EphemeralInstanceGroup.SelectGroupOthers(new[] { primary }, primary, f => f.Instance, f => f.Occupied, out var occupied);
            Assert.AreEqual(0, alone.Count, "the primary is excluded by reference, even though it is occupied");
            Assert.AreEqual(0, occupied);
            Assert.AreEqual(EphemeralInstanceGroup.PrimaryUnloadDecision.Unload, EphemeralInstanceGroup.DecidePrimaryUnload(alone.Count, occupied));

            var withLookalike = EphemeralInstanceGroup.SelectGroupOthers(new[] { primary, lookalike }, primary, f => f.Instance, f => f.Occupied, out occupied);
            Assert.AreEqual(1, withLookalike.Count, "a different object under the same instance is another group landblock");
            Assert.AreEqual(EphemeralInstanceGroup.PrimaryUnloadDecision.DrainGroupFirst, EphemeralInstanceGroup.DecidePrimaryUnload(withLookalike.Count, occupied));
        }
    }
}
