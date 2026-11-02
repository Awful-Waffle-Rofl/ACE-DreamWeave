using ACE.Entity;
using ACE.Server.Entity;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// <see cref="LandblockGroupClustering.MayCluster(int, int, bool, uint, int, int, bool, uint)"/>, the single pairwise
    /// rule LandblockGroup grows by and LandblockGroupSplitHelper splits by: distance, dormancy and the instance-group
    /// tick key (Open World Threads P0). No PropertyManager keys are read.
    /// </summary>
    [TestClass]
    public class LandblockGroupClusteringTests
    {
        private static readonly uint BaseWorld = Position.InstanceIDFromVars(0, 0, false);
        private static readonly uint Realm1 = Position.InstanceIDFromVars(1, 0, false);
        private static readonly uint Ephemeral = Position.InstanceIDFromVars(0, 0x1234, true);
        private static readonly uint OtherEphemeral = Position.InstanceIDFromVars(0, 0x4321, true);

        private static bool Pair(int dx, bool aDormant, bool bDormant, uint a, uint b) =>
            LandblockGroupClustering.MayCluster(0x80, 0x80, aDormant, a, 0x80 + dx, 0x80, bDormant, b);

        [TestMethod]
        public void SameKey_Near_Clusters()
        {
            Assert.IsTrue(Pair(1, false, false, BaseWorld, BaseWorld));
            Assert.IsTrue(Pair(LandblockGroup.LandblockGroupMinSpacing - 1, false, false, BaseWorld, BaseWorld), "just inside the active spacing");
            Assert.IsTrue(Pair(1, false, false, Ephemeral, Ephemeral), "one copy's own landblocks cluster");
            Assert.IsTrue(Pair(1, false, false, BaseWorld, Realm1), "persistent realms still share a key, as before P0");
        }

        [TestMethod]
        public void SameKey_Far_DoesNotCluster()
        {
            Assert.IsFalse(Pair(LandblockGroup.LandblockGroupMinSpacing, false, false, BaseWorld, BaseWorld), "the spacing bound is strict");
            Assert.IsFalse(Pair(10, false, false, Ephemeral, Ephemeral));
        }

        [TestMethod]
        public void DifferentKey_Near_DoesNotCluster()
        {
            Assert.IsFalse(Pair(0, false, false, Ephemeral, BaseWorld), "an outdoor copy must not join the real world's group even on the same landblock id");
            Assert.IsFalse(Pair(1, false, false, BaseWorld, Ephemeral), "symmetric");
            Assert.IsFalse(Pair(1, false, false, Ephemeral, OtherEphemeral), "two copies stay apart");
        }

        [TestMethod]
        public void Dormant_UsesTheTighterSpacing()
        {
            var between = LandblockGroup.LandblockGroupMinSpacingWhenDormant;
            Assert.IsTrue(between < LandblockGroup.LandblockGroupMinSpacing, "test setup: dormant spacing is the tighter one");

            Assert.IsTrue(Pair(between, false, false, BaseWorld, BaseWorld), "active pair at the dormant spacing clusters");
            Assert.IsFalse(Pair(between, true, false, BaseWorld, BaseWorld), "a dormant A uses the tighter spacing");
            Assert.IsFalse(Pair(between, false, true, BaseWorld, BaseWorld), "a dormant B uses the tighter spacing");
            Assert.IsTrue(Pair(between - 1, true, true, BaseWorld, BaseWorld), "inside the dormant spacing still clusters");
        }
    }
}
