using System.Collections.Generic;
using System.Linq;

using ACE.Server.WorldEvents;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Unit coverage for <see cref="WorldEventLandblockHold.ComputeRelease"/> (TECH-DESIGN 2.11, 5.6, R6).
    ///
    /// This one function is the only thing standing between an event ending and a config-permaloaded town
    /// block silently unloading five minutes later, because Landblock.Permaload carries no reference count
    /// and "release" means assigning it false.
    /// </summary>
    [TestClass]
    public class WorldEventLandblockHoldTests
    {
        private const ulong AnchorKey = 0x0000000001C4FFFF;
        private const ulong AdjacentKey = 0x0000000001C5FFFF;
        private const ulong TownKey = 0x00000000E74EFFFF;

        // A realm copy of the same landblock: same id, different instance, therefore a different key.
        private const ulong AnchorKeyOtherInstance = 0x0000000201C4FFFF;

        [TestMethod]
        public void ComputeRelease_KeyThatWasAlreadyPermaloaded_IsNeverReleased()
        {
            var prior = new Dictionary<ulong, bool> { [TownKey] = true };

            var release = WorldEventLandblockHold.ComputeRelease(prior, new List<ulong> { TownKey });

            Assert.AreEqual(0, release.Count, "a block that was already permaloaded must never be cleared");
        }

        [TestMethod]
        public void ComputeRelease_KeyThatWasLoadedButNotPermaloaded_IsReleased()
        {
            var prior = new Dictionary<ulong, bool> { [AnchorKey] = false };

            var release = WorldEventLandblockHold.ComputeRelease(prior, new List<ulong> { AnchorKey });

            CollectionAssert.AreEqual(new List<ulong> { AnchorKey }, release.ToList());
        }

        [TestMethod]
        public void ComputeRelease_KeyAbsentFromThePriorMap_CountsAsNotPermaloaded_AndIsReleased()
        {
            // Absent means the block was not loaded at all before the hold, so the event is what brought it
            // in and the event is what takes it back out.
            var prior = new Dictionary<ulong, bool> { [TownKey] = true };

            var release = WorldEventLandblockHold.ComputeRelease(prior, new List<ulong> { AnchorKey });

            CollectionAssert.AreEqual(new List<ulong> { AnchorKey }, release.ToList());
        }

        [TestMethod]
        public void ComputeRelease_MixedHold_ReleasesOnlyTheBlocksTheEventBroughtIn()
        {
            var prior = new Dictionary<ulong, bool>
            {
                [TownKey] = true,       // config permaload - untouchable
                [AdjacentKey] = false   // loaded, but not permaloaded
                                        // AnchorKey absent - not loaded at all
            };

            var held = new List<ulong> { AnchorKey, AdjacentKey, TownKey };

            var release = WorldEventLandblockHold.ComputeRelease(prior, held);

            CollectionAssert.AreEqual(new List<ulong> { AnchorKey, AdjacentKey }, release.ToList(),
                "input order must be preserved and the config permaload must be excluded");
        }

        [TestMethod]
        public void ComputeRelease_CollapsesDuplicates()
        {
            var release = WorldEventLandblockHold.ComputeRelease(
                new Dictionary<ulong, bool>(),
                new List<ulong> { AnchorKey, AnchorKey, AdjacentKey, AnchorKey });

            CollectionAssert.AreEqual(new List<ulong> { AnchorKey, AdjacentKey }, release.ToList());
        }

        [TestMethod]
        public void ComputeRelease_DuplicateOfAPermaloadedKey_StaysExcluded()
        {
            var prior = new Dictionary<ulong, bool> { [TownKey] = true };

            var release = WorldEventLandblockHold.ComputeRelease(prior, new List<ulong> { TownKey, TownKey });

            Assert.AreEqual(0, release.Count);
        }

        [TestMethod]
        public void ComputeRelease_DistinguishesInstancesOfTheSameLandblock()
        {
            // The key is (instance << 32) | id, so a realm copy of a permaloaded block is a DIFFERENT block
            // and releasing it must not be blocked by the base-world copy's state.
            var prior = new Dictionary<ulong, bool> { [AnchorKey] = true };

            var release = WorldEventLandblockHold.ComputeRelease(prior,
                new List<ulong> { AnchorKey, AnchorKeyOtherInstance });

            CollectionAssert.AreEqual(new List<ulong> { AnchorKeyOtherInstance }, release.ToList());
        }

        [TestMethod]
        public void ComputeRelease_EmptyAndNullInputs_ReturnEmpty()
        {
            Assert.AreEqual(0, WorldEventLandblockHold.ComputeRelease(null, null).Count);
            Assert.AreEqual(0, WorldEventLandblockHold.ComputeRelease(null, new List<ulong>()).Count);
            Assert.AreEqual(0, WorldEventLandblockHold.ComputeRelease(new Dictionary<ulong, bool>(), null).Count);
        }

        [TestMethod]
        public void ComputeRelease_NullPriorMap_ReleasesEverythingHeld()
        {
            // A null snapshot is "nothing is known to be permaloaded", which is the safe-for-the-event side
            // of the trade only because the hold itself always takes a real snapshot first.
            var release = WorldEventLandblockHold.ComputeRelease(null, new List<ulong> { AnchorKey, TownKey });

            CollectionAssert.AreEqual(new List<ulong> { AnchorKey, TownKey }, release.ToList());
        }
    }
}
