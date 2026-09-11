using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Database.Models.World;
using ACE.Entity.Enum;
using ACE.Server.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    [TestClass]
    public class ThreadDungeonContentFilterTests
    {
        private const uint ExitPortal = 1003601;

        private static LandblockInstance Row(uint guid, uint wcid, uint cell = 0x01500100) => new LandblockInstance
        {
            Guid = guid, WeenieClassId = wcid, ObjCellId = cell, OriginX = 1, OriginY = 2, OriginZ = 3, AnglesW = 1, IsLinkChild = false,
        };

        private static LandblockInstance LinkChildRow(uint guid, uint wcid)
        {
            var row = Row(guid, wcid);
            row.IsLinkChild = true;
            return row;
        }

        private static readonly Dictionary<uint, WeenieType> Types = new Dictionary<uint, WeenieType>
        {
            [1] = WeenieType.Generic, [2] = WeenieType.Creature, [3] = WeenieType.Door, [4] = WeenieType.Switch,
            [5] = WeenieType.Portal, [6] = WeenieType.Chest, [7] = WeenieType.HotSpot, [8] = WeenieType.PressurePlate, [9] = WeenieType.Generic,
            // the rest of the container family (TECH-DESIGN rule 2) and the second portal type
            [10] = WeenieType.Container, [11] = WeenieType.Storage, [12] = WeenieType.SlumLord, [13] = WeenieType.Hook,
            [14] = WeenieType.HousePortal, [15] = WeenieType.LifeStone,
        };

        private static WeenieType TypeOf(uint wcid) => Types[wcid];
        private static bool IsGenerator(uint wcid) => wcid == 9;

        private static List<LandblockInstance> Sample() => new List<LandblockInstance>
        {
            Row(0x71500001, 1), Row(0x71500002, 2), Row(0x71500003, 3), Row(0x71500004, 4),
            Row(0x71500005, 5), Row(0x71500006, 6), Row(0x71500007, 7), Row(0x71500008, 8), Row(0x71500009, 9),
        };

        [TestMethod]
        public void Drops_generators_creatures_and_chests()
        {
            var kept = ThreadDungeonContentFilter.Filter(Sample(), TypeOf, IsGenerator, ExitPortal, out var stats);
            var wcids = kept.Select(k => k.WeenieClassId).ToList();
            CollectionAssert.DoesNotContain(wcids, 2u);
            CollectionAssert.DoesNotContain(wcids, 6u);
            CollectionAssert.DoesNotContain(wcids, 9u);
            Assert.AreEqual(1, stats.DroppedGenerators);
            Assert.AreEqual(1, stats.DroppedCreatures);
            Assert.AreEqual(1, stats.DroppedContainers);
        }

        [TestMethod]
        public void Keeps_doors_switches_plates_hotspots_and_generics_by_reference()
        {
            var source = Sample();
            var kept = ThreadDungeonContentFilter.Filter(source, TypeOf, IsGenerator, ExitPortal, out var stats);
            foreach (var wcid in new uint[] { 1, 3, 4, 7, 8 })
                Assert.IsTrue(kept.Any(k => ReferenceEquals(k, source.First(s => s.WeenieClassId == wcid))), $"wcid {wcid} must be the same row object");
            Assert.AreEqual(5, stats.Kept);
        }

        [TestMethod]
        public void Replaces_portals_with_the_exit_portal_clone()
        {
            var source = Sample();
            var kept = ThreadDungeonContentFilter.Filter(source, TypeOf, IsGenerator, ExitPortal, out var stats);
            var portal = kept.Single(k => k.Guid == 0x71500005);
            Assert.AreEqual(ExitPortal, portal.WeenieClassId);
            Assert.AreEqual(0x01500100u, portal.ObjCellId);
            Assert.AreEqual(1f, portal.OriginX);
            Assert.AreEqual(0, portal.LandblockInstanceLink.Count);
            Assert.IsFalse(ReferenceEquals(portal, source[4]), "the clone must not be the cached row");
            Assert.AreEqual(5u, source[4].WeenieClassId, "the cached row is untouched");
            Assert.AreEqual(1, stats.ReplacedPortals);
        }

        [TestMethod]
        public void Source_list_is_never_mutated()
        {
            var source = Sample();
            var before = source.Select(s => (s.Guid, s.WeenieClassId)).ToList();
            ThreadDungeonContentFilter.Filter(source, TypeOf, IsGenerator, ExitPortal, out _);
            CollectionAssert.AreEqual(before, source.Select(s => (s.Guid, s.WeenieClassId)).ToList());
            Assert.AreEqual(9, source.Count);
        }

        [TestMethod]
        public void Unknown_weenie_type_is_dropped_and_counted()
        {
            var source = new List<LandblockInstance> { Row(0x71500010, 42) };
            var kept = ThreadDungeonContentFilter.Filter(source, w => throw new KeyNotFoundException(), _ => false, ExitPortal, out var stats);
            Assert.AreEqual(0, kept.Count);
            Assert.AreEqual(1, stats.DroppedUnresolved);
        }

        /// <summary>
        /// F3: only a KeyNotFoundException means "no such weenie". A world-db failure on a cold cache must
        /// reach the caller, so the landblock fails loudly instead of loading a half-built copy.
        /// </summary>
        [TestMethod]
        public void A_non_KeyNotFound_failure_propagates()
        {
            var source = new List<LandblockInstance> { Row(0x71500010, 42) };
            try
            {
                ThreadDungeonContentFilter.Filter(source, w => throw new InvalidOperationException("world db is down"), _ => false, ExitPortal, out _);
                Assert.Fail("the filter must not swallow a non-KeyNotFoundException");
            }
            catch (InvalidOperationException ex)
            {
                Assert.AreEqual("world db is down", ex.Message);
            }
        }

        /// <summary>F4a: the drop set is the whole container family, not just Chest.</summary>
        [TestMethod]
        public void Drops_the_whole_container_family()
        {
            var source = new List<LandblockInstance>
            {
                Row(0x71500006, 6),  // Chest
                Row(0x71500010, 10), // Container
                Row(0x71500011, 11), // Storage
                Row(0x71500012, 12), // SlumLord
                Row(0x71500013, 13), // Hook
                Row(0x71500015, 15), // LifeStone - kept, proves the arm is not swallowing everything
            };

            var kept = ThreadDungeonContentFilter.Filter(source, TypeOf, IsGenerator, ExitPortal, out var stats);

            Assert.AreEqual(5, stats.DroppedContainers);
            Assert.AreEqual(1, stats.Kept);
            Assert.AreEqual(15u, kept.Single().WeenieClassId);
        }

        /// <summary>
        /// F1: a portal weenie that also carries a generator table must be REPLACED, not dropped - dropping
        /// it would leave the run with no way out. One exists in the world db (wcid 10792, landblock 0xBE89).
        /// </summary>
        [TestMethod]
        public void Portal_carrying_a_generator_table_is_replaced_not_dropped()
        {
            var source = new List<LandblockInstance> { Row(0x71500005, 5) };

            var kept = ThreadDungeonContentFilter.Filter(source, TypeOf, _ => true, ExitPortal, out var stats);

            Assert.AreEqual(1, stats.ReplacedPortals);
            Assert.AreEqual(0, stats.DroppedGenerators);
            Assert.AreEqual(ExitPortal, kept.Single().WeenieClassId);
        }

        /// <summary>F2: HousePortal is replaced exactly like Portal.</summary>
        [TestMethod]
        public void House_portal_is_replaced_like_a_portal()
        {
            var source = new List<LandblockInstance> { Row(0x71500014, 14) };

            var kept = ThreadDungeonContentFilter.Filter(source, TypeOf, IsGenerator, ExitPortal, out var stats);

            Assert.AreEqual(1, stats.ReplacedPortals);
            Assert.AreEqual(ExitPortal, kept.Single().WeenieClassId);
            Assert.AreEqual(0x71500014u, kept.Single().Guid, "the clone keeps the row's guid");
            Assert.IsFalse(ReferenceEquals(kept.Single(), source[0]), "the clone must not be the cached row");
        }

        /// <summary>
        /// F6: IsLinkChild plays no part in the decision - a link child is judged on its own weenie, so a
        /// child Switch survives by reference and a child Creature is dropped and counted.
        /// </summary>
        [TestMethod]
        public void Link_child_rows_are_judged_on_their_own_weenie()
        {
            var childSwitch = LinkChildRow(0x71500004, 4);
            var childCreature = LinkChildRow(0x71500002, 2);
            var source = new List<LandblockInstance> { childSwitch, childCreature };

            var kept = ThreadDungeonContentFilter.Filter(source, TypeOf, IsGenerator, ExitPortal, out var stats);

            Assert.AreEqual(1, kept.Count);
            Assert.IsTrue(ReferenceEquals(kept[0], childSwitch), "a link-child Switch must be the same row object");
            Assert.IsTrue(kept[0].IsLinkChild, "IsLinkChild is carried through untouched");
            Assert.AreEqual(1, stats.Kept);
            Assert.AreEqual(1, stats.DroppedCreatures);
        }
    }
}
