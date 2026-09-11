using System.Collections.Generic;

using ACE.Entity;
using ACE.Server.Entity;
using ACE.Server.WorldEvents;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Pure coverage for <see cref="WorldEventTownIndex"/>: portal-name normalisation, nearest-town search
    /// and the player-facing place text. Load() touches the world database and is not tested here (D6).
    /// Town positions below are the real Town Network destinations (landblock 0x0007 portal weenies).
    /// </summary>
    [TestClass]
    public class WorldEventTownIndexTests
    {
        // Portal to Holtburg (wcid 42820) destination A9B40019 84.0 7.1 94.0
        private static Position HoltburgDrop() => new Position(0xA9B40019, 84f, 7.1f, 94f, 0, 0, 0, 1, 0);

        // Portal to Shoushi (wcid 42840) destination DA55001D 84.8 99.0 20.0
        private static Position ShoushiDrop() => new Position(0xDA55001D, 84.8f, 99f, 20f, 0, 0, 0, 1, 0);

        private static List<WorldEventTown> Towns()
        {
            return new List<WorldEventTown>
            {
                WorldEventTownIndex.FromPortal("Portal to Holtburg", HoltburgDrop()).Value,
                WorldEventTownIndex.FromPortal("Portal to Shoushi", ShoushiDrop()).Value,
            };
        }

        [TestMethod]
        public void NormalizeTownName_StripsPortalPrefixAndSuffix()
        {
            Assert.AreEqual("Holtburg", WorldEventTownIndex.NormalizeTownName("Portal to Holtburg"));
            Assert.AreEqual("Ayan Baqur", WorldEventTownIndex.NormalizeTownName("Ayan Baqur Portal"));
            Assert.AreEqual("Asheron's Castle", WorldEventTownIndex.NormalizeTownName("Asheron's Castle"));
            Assert.AreEqual("Danby's Outpost", WorldEventTownIndex.NormalizeTownName("  Danby's Outpost "));
            Assert.IsNull(WorldEventTownIndex.NormalizeTownName(null));
            Assert.IsNull(WorldEventTownIndex.NormalizeTownName("   "));
            Assert.IsNull(WorldEventTownIndex.NormalizeTownName("Portal to "));
        }

        [TestMethod]
        public void FromPortal_RejectsIndoorDestinationsAndMissingData()
        {
            // The Marketplace of Dereth (wcid 23032) drops into cell 016C01BC - indoors, no map position.
            var marketplace = new Position(0x016C01BC, 49.2f, -31.9f, 0f, 0, 0, 0, 1, 0);

            Assert.IsNull(WorldEventTownIndex.FromPortal("The Marketplace of Dereth", marketplace));
            Assert.IsNull(WorldEventTownIndex.FromPortal("Portal to Holtburg", null));
            Assert.IsNull(WorldEventTownIndex.FromPortal(null, HoltburgDrop()));

            var holtburg = WorldEventTownIndex.FromPortal("Portal to Holtburg", HoltburgDrop());

            Assert.IsNotNull(holtburg);
            Assert.AreEqual("Holtburg", holtburg.Value.Name);
            Assert.AreEqual(0xA9 * 192f + 84f, holtburg.Value.GlobalX, 0.01f);
            Assert.AreEqual(0xB4 * 192f + 7.1f, holtburg.Value.GlobalY, 0.01f);
        }

        [TestMethod]
        public void Nearest_PicksTheClosestTownByGlobalDistance()
        {
            var towns = Towns();

            var atHoltburg = WorldEventTownIndex.Nearest(HoltburgDrop(), towns);
            Assert.IsNotNull(atHoltburg);
            Assert.AreEqual("Holtburg", atHoltburg.Value.Town.Name);
            Assert.AreEqual(0f, atHoltburg.Value.Distance, 0.01f);

            // Two landblocks north of the Holtburg drop (2 x 192 m) - still Holtburg, distance 384 m.
            var north = new Position(0xA9B60019, 84f, 7.1f, 94f, 0, 0, 0, 1, 0);
            var nearHoltburg = WorldEventTownIndex.Nearest(north, towns);
            Assert.AreEqual("Holtburg", nearHoltburg.Value.Town.Name);
            Assert.AreEqual(384f, nearHoltburg.Value.Distance, 0.01f);

            var atShoushi = WorldEventTownIndex.Nearest(ShoushiDrop(), towns);
            Assert.AreEqual("Shoushi", atShoushi.Value.Town.Name);
        }

        [TestMethod]
        public void Nearest_IsNullForIndoorsOrEmptyList()
        {
            var indoors = new Position(0x016C01BC, 49.2f, -31.9f, 0f, 0, 0, 0, 1, 0);

            Assert.IsNull(WorldEventTownIndex.Nearest(indoors, Towns()));
            Assert.IsNull(WorldEventTownIndex.Nearest(HoltburgDrop(), new List<WorldEventTown>()));
            Assert.IsNull(WorldEventTownIndex.Nearest(HoltburgDrop(), null));
            Assert.IsNull(WorldEventTownIndex.Nearest(null, Towns()));
        }

        [TestMethod]
        public void Describe_AtTown_NamesTownWithCoordinates()
        {
            var pos = HoltburgDrop();

            var text = WorldEventTownIndex.Describe(pos, Towns());

            Assert.AreEqual($"Holtburg ({pos.GetMapCoordStr()})", text);
        }

        [TestMethod]
        public void Describe_FarFromTown_StillNamesNearestTownWithTheEventCoordinates()
        {
            // 10 landblocks (1920 m) north of the Holtburg drop: nearest town is still Holtburg, and the
            // coordinates are the EVENT's, not the town's.
            var pos = new Position(0xA9BE0019, 84f, 7.1f, 94f, 0, 0, 0, 1, 0);

            var text = WorldEventTownIndex.Describe(pos, Towns());

            Assert.AreEqual($"Holtburg ({pos.GetMapCoordStr()})", text);
            Assert.AreNotEqual(HoltburgDrop().GetMapCoordStr(), pos.GetMapCoordStr());
        }

        [TestMethod]
        public void Describe_NoTowns_FallsBackToCoordinates()
        {
            var pos = HoltburgDrop();

            Assert.AreEqual(pos.GetMapCoordStr(), WorldEventTownIndex.Describe(pos, new List<WorldEventTown>()));
            Assert.AreEqual(pos.GetMapCoordStr(), WorldEventTownIndex.Describe(pos, null));
        }

        [TestMethod]
        public void Describe_Indoors_HasNoCoordinatesOrTown()
        {
            var indoors = new Position(0x016C01BC, 49.2f, -31.9f, 0f, 0, 0, 0, 1, 0);

            Assert.AreEqual("an underground location", WorldEventTownIndex.Describe(indoors, Towns()));
            Assert.AreEqual("an underground location", WorldEventTownIndex.Describe(null, Towns()));
        }

        [TestMethod]
        public void Describe_LiveOverload_UsesTheSetList()
        {
            var previous = WorldEventTownIndex.Towns;

            try
            {
                WorldEventTownIndex.SetTowns(Towns());

                Assert.AreEqual($"Holtburg ({HoltburgDrop().GetMapCoordStr()})", WorldEventTownIndex.Describe(HoltburgDrop()));

                WorldEventTownIndex.SetTowns(null);

                Assert.AreEqual(HoltburgDrop().GetMapCoordStr(), WorldEventTownIndex.Describe(HoltburgDrop()));
            }
            finally
            {
                WorldEventTownIndex.SetTowns(previous);
            }
        }
    }
}
