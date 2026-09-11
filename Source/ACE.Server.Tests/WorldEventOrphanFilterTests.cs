using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Models.Shard;
using ACE.Entity.Enum.Properties;
using ACE.Server.WorldEvents;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The world-event orphan filter (TECH-DESIGN 2.9). Only the two pure members are exercised here -
    /// IsWorldEventOrphan and Partition - because they are the whole decision; FilterAndSweep adds nothing but
    /// logging and a queued shard delete, and neither a Landblock nor a database can be stood up in this suite.
    ///
    /// Everything below is built from ACE.Database.Models.Shard.Biota, the EF entity with a BiotaPropertiesInt
    /// collection of Type/Value rows. That is deliberately NOT ACE.Entity.Models.Biota, whose PropertiesInt is a
    /// dictionary - the two are the standing confusion in this codebase, and a filter written against the wrong
    /// one would compile against neither.
    /// </summary>
    [TestClass]
    public class WorldEventOrphanFilterTests
    {
        private const ushort WorldEventId = (ushort)PropertyInt.WorldEventId;

        /// <summary>A neighbouring fork-allocated PropertyInt that must NOT trip the filter.</summary>
        private const ushort WorldEventsCompleted = (ushort)PropertyInt.WorldEventsCompleted;

        private static Biota MakeBiota(uint id, params (ushort type, int value)[] ints)
        {
            var biota = new Biota { Id = id, WeenieClassId = 1, WeenieType = 10 };

            foreach (var (type, value) in ints)
                biota.BiotaPropertiesInt.Add(new BiotaPropertiesInt { ObjectId = id, Type = type, Value = value });

            return biota;
        }

        // ---- IsWorldEventOrphan ----

        [TestMethod]
        public void Biota_WithTheMarker_IsAnOrphan()
        {
            Assert.IsTrue(WorldEventOrphanFilter.IsWorldEventOrphan(MakeBiota(0x80000001, (WorldEventId, 17))));
        }

        [TestMethod]
        public void Biota_WithoutTheMarker_IsKept()
        {
            // a plausible corpse: level and encumbrance, no event stamp
            Assert.IsFalse(WorldEventOrphanFilter.IsWorldEventOrphan(MakeBiota(0x80000002, (25, 130), (19, 4000))));
        }

        [TestMethod]
        public void Biota_WithNoIntPropertiesAtAll_IsKept()
        {
            Assert.IsFalse(WorldEventOrphanFilter.IsWorldEventOrphan(MakeBiota(0x80000003)));
        }

        [TestMethod]
        public void TheStampIsPresence_SoAnyValueCounts()
        {
            // The value is the RunId of the run that spawned the object. A run id from a dead process means
            // nothing now, so every value - including 0 and a negative - is still an orphan (TECH-DESIGN 2.13).
            foreach (var value in new[] { 0, 1, -1, int.MaxValue, int.MinValue })
            {
                Assert.IsTrue(
                    WorldEventOrphanFilter.IsWorldEventOrphan(MakeBiota(0x80000004, (WorldEventId, value))),
                    $"WorldEventId = {value} should still be an orphan");
            }
        }

        [TestMethod]
        public void Biota_CarryingOtherNineThousandInts_ButNot9041_IsKept()
        {
            // 9042 and 9043 sit either side of the 9041 marker and are legitimate properties that do persist.
            // (That the marker really is 9041, and matches Source/property-registry.tsv, is PropertyRegistryTests'
            // job - asserting it again here would be a compile-time tautology.)
            var biota = MakeBiota(0x80000005, (WorldEventsCompleted, 3), (9043, 2));

            Assert.IsFalse(WorldEventOrphanFilter.IsWorldEventOrphan(biota));
        }

        [TestMethod]
        public void MarkerAnywhereInTheCollection_IsFound()
        {
            var biota = MakeBiota(0x80000006, (25, 130), (19, 4000), (WorldEventId, 9));

            Assert.IsTrue(WorldEventOrphanFilter.IsWorldEventOrphan(biota));
        }

        [TestMethod]
        public void NullBiota_IsNotAnOrphan()
        {
            Assert.IsFalse(WorldEventOrphanFilter.IsWorldEventOrphan(null));
        }

        // ---- Partition ----

        [TestMethod]
        public void Partition_EmptyList_KeepsNothingAndSweepsNothing()
        {
            WorldEventOrphanFilter.Partition(new List<Biota>(), out var kept, out var orphanIds);

            Assert.AreEqual(0, kept.Count);
            Assert.AreEqual(0, orphanIds.Count);
        }

        [TestMethod]
        public void Partition_NullList_IsSafe()
        {
            WorldEventOrphanFilter.Partition(null, out var kept, out var orphanIds);

            Assert.IsNotNull(kept);
            Assert.AreEqual(0, kept.Count);
            Assert.AreEqual(0, orphanIds.Count);
        }

        [TestMethod]
        public void Partition_NoOrphans_ReturnsEveryBiotaAndAllocatesNothing()
        {
            var dynamics = new List<Biota>
            {
                MakeBiota(0x80000010, (25, 130)),
                MakeBiota(0x80000011),
                MakeBiota(0x80000012, (WorldEventsCompleted, 1)),
            };

            WorldEventOrphanFilter.Partition(dynamics, out var kept, out var orphanIds);

            Assert.AreEqual(0, orphanIds.Count);
            Assert.AreEqual(3, kept.Count);

            // documented fast path: the clean case hands back the same list, it does not copy
            Assert.AreSame(dynamics, kept);
        }

        [TestMethod]
        public void Partition_SplitsOrphansOutAndReportsTheirGuids()
        {
            var dynamics = new List<Biota>
            {
                MakeBiota(0x80000020, (25, 130)),
                MakeBiota(0x80000021, (WorldEventId, 4)),
                MakeBiota(0x80000022),
                MakeBiota(0x80000023, (WorldEventId, 0)),
            };

            WorldEventOrphanFilter.Partition(dynamics, out var kept, out var orphanIds);

            CollectionAssert.AreEqual(new uint[] { 0x80000020, 0x80000022 }, kept.Select(b => b.Id).ToArray());
            CollectionAssert.AreEqual(new uint[] { 0x80000021, 0x80000023 }, orphanIds.ToArray());

            // the caller hands `kept` to the factory, so the orphans must be gone from it, not merely listed
            Assert.IsFalse(kept.Any(WorldEventOrphanFilter.IsWorldEventOrphan));

            // and the input is left alone - Partition must not mutate what the database handed the landblock
            Assert.AreEqual(4, dynamics.Count);
        }

        [TestMethod]
        public void Partition_EveryBiotaIsAnOrphan_KeepsNothing()
        {
            var dynamics = new List<Biota>
            {
                MakeBiota(0x80000030, (WorldEventId, 1)),
                MakeBiota(0x80000031, (WorldEventId, 2)),
            };

            WorldEventOrphanFilter.Partition(dynamics, out var kept, out var orphanIds);

            Assert.AreEqual(0, kept.Count);
            CollectionAssert.AreEqual(new uint[] { 0x80000030, 0x80000031 }, orphanIds.ToArray());
        }
    }
}
