using System.Numerics;

using ACE.Server.MlTreasure;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.MlTreasure
{
    /// <summary>
    /// Pure map-coordinate math (TREASURE-HUNT-PLAN.md section 6): metres conversion, stage
    /// classification against the far/near thresholds, and the 8-point bearing. All inputs are plain
    /// Vector2 map coordinates (X=EastWest, Y=NorthSouth) - no PropertyManager, no world state.
    /// </summary>
    [TestClass]
    public class MlTreasureGeometryTests
    {
        // ---- DistanceMetres --------------------------------------------------------------------------

        [TestMethod]
        public void DistanceMetres_SameCoords_IsZero()
        {
            var p = new Vector2(3f, -5f);

            Assert.AreEqual(0f, MlTreasureGeometry.DistanceMetres(p, p), 1e-4f);
        }

        /// <summary>Pins the map-coordinate-space distance rule itself: 1 map unit = 240 m
        /// (TREASURE-HUNT-PLAN.md section 6), so a 1-map-unit EW delta is exactly 240 m, never the
        /// player-to-Position metre distance a reconstructed Position would give.</summary>
        [TestMethod]
        public void DistanceMetres_OneMapUnitDelta_Is240Metres()
        {
            var player = new Vector2(0f, 0f);
            var site = new Vector2(1f, 0f);

            Assert.AreEqual(240f, MlTreasureGeometry.DistanceMetres(player, site), 1e-3f);
        }

        [TestMethod]
        public void DistanceMetres_3_4_5Triangle_MatchesPythagoras()
        {
            var player = new Vector2(0f, 0f);
            var site = new Vector2(3f, 4f);

            // 5 map units * 240 m/unit = 1200 m
            Assert.AreEqual(1200f, MlTreasureGeometry.DistanceMetres(player, site), 1e-3f);
        }

        // ---- Classify --------------------------------------------------------------------------------

        [TestMethod]
        public void Classify_BeyondFar_IsFarStage()
        {
            Assert.AreEqual(MlTreasureGeometry.Stage.Far, MlTreasureGeometry.Classify(501f, farMetres: 500f, nearMetres: 40f));
        }

        [TestMethod]
        public void Classify_AtFarThreshold_IsCardinalStage()
        {
            // "beyond the far threshold" -> exactly at the threshold is NOT beyond it
            Assert.AreEqual(MlTreasureGeometry.Stage.Cardinal, MlTreasureGeometry.Classify(500f, farMetres: 500f, nearMetres: 40f));
        }

        [TestMethod]
        public void Classify_BetweenNearAndFar_IsCardinalStage()
        {
            Assert.AreEqual(MlTreasureGeometry.Stage.Cardinal, MlTreasureGeometry.Classify(200f, farMetres: 500f, nearMetres: 40f));
        }

        [TestMethod]
        public void Classify_AtNearThreshold_IsDigStage()
        {
            Assert.AreEqual(MlTreasureGeometry.Stage.Dig, MlTreasureGeometry.Classify(40f, farMetres: 500f, nearMetres: 40f));
        }

        [TestMethod]
        public void Classify_WithinNear_IsDigStage()
        {
            Assert.AreEqual(MlTreasureGeometry.Stage.Dig, MlTreasureGeometry.Classify(0f, farMetres: 500f, nearMetres: 40f));
        }

        // ---- Bearing8 --------------------------------------------------------------------------------

        [TestMethod]
        public void Bearing8_SiteDueNorth_ReadsNorth()
        {
            var player = new Vector2(0f, 0f);
            var site = new Vector2(0f, 10f);

            Assert.AreEqual("north", MlTreasureGeometry.Bearing8(player, site));
        }

        [TestMethod]
        public void Bearing8_SiteDueEast_ReadsEast()
        {
            var player = new Vector2(0f, 0f);
            var site = new Vector2(10f, 0f);

            Assert.AreEqual("east", MlTreasureGeometry.Bearing8(player, site));
        }

        [TestMethod]
        public void Bearing8_SiteDueSouth_ReadsSouth()
        {
            var player = new Vector2(0f, 0f);
            var site = new Vector2(0f, -10f);

            Assert.AreEqual("south", MlTreasureGeometry.Bearing8(player, site));
        }

        [TestMethod]
        public void Bearing8_SiteDueWest_ReadsWest()
        {
            var player = new Vector2(0f, 0f);
            var site = new Vector2(-10f, 0f);

            Assert.AreEqual("west", MlTreasureGeometry.Bearing8(player, site));
        }

        [TestMethod]
        public void Bearing8_SiteNortheast_ReadsNortheast()
        {
            var player = new Vector2(0f, 0f);
            var site = new Vector2(10f, 10f);

            Assert.AreEqual("northeast", MlTreasureGeometry.Bearing8(player, site));
        }

        [TestMethod]
        public void Bearing8_SiteSoutheast_ReadsSoutheast()
        {
            var player = new Vector2(0f, 0f);
            var site = new Vector2(10f, -10f);

            Assert.AreEqual("southeast", MlTreasureGeometry.Bearing8(player, site));
        }

        [TestMethod]
        public void Bearing8_SiteSouthwest_ReadsSouthwest()
        {
            var player = new Vector2(0f, 0f);
            var site = new Vector2(-10f, -10f);

            Assert.AreEqual("southwest", MlTreasureGeometry.Bearing8(player, site));
        }

        [TestMethod]
        public void Bearing8_SiteNorthwest_ReadsNorthwest()
        {
            var player = new Vector2(0f, 0f);
            var site = new Vector2(-10f, 10f);

            Assert.AreEqual("northwest", MlTreasureGeometry.Bearing8(player, site));
        }

        [TestMethod]
        public void Bearing8_ZeroDelta_ReadsNorth_DefinedButUnreached()
        {
            var p = new Vector2(5f, 5f);

            Assert.AreEqual("north", MlTreasureGeometry.Bearing8(p, p));
        }
    }
}
