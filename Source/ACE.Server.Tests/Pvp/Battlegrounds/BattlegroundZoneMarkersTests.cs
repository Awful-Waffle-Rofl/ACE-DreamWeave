using System;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Pvp.Battlegrounds;

namespace ACE.Server.Tests.Pvp.Battlegrounds
{
    /// <summary>
    /// The King of the Hill zone-marker planner (Docs/Pvp/BATTLEGROUNDS.md "Zone markers"). Every test builds the zone
    /// through BattlegroundModes.KothZoneFor, the same call the scoring cylinder is built with.
    /// </summary>
    [TestClass]
    public class BattlegroundZoneMarkersTests
    {
        private const uint MarkerWcid = 4242;

        /// <summary>The shipped map with a non-zero marker wcid (the catalog still carries the placeholder 0).</summary>
        private static readonly BattlegroundLayout Layout = BattlegroundMapCatalog.Bg016c with { ZoneMarkerWcid = MarkerWcid };

        private static KothZone ZoneFor(BattlegroundDials d) => BattlegroundModes.KothZoneFor(Layout, d);

        private static double Dist(BattlegroundSealPiece p, KothZone z) => Math.Sqrt((p.X - z.CenterX) * (p.X - z.CenterX) + (p.Y - z.CenterY) * (p.Y - z.CenterY));

        [TestMethod]
        public void DefaultDials_Give16Markers()
        {
            var d = BattlegroundTunables.Defaults;

            // ceil(2 pi 6 / 2.5) = ceil(15.08) = 16
            Assert.AreEqual(16, BattlegroundZoneMarkers.Plan(Layout, ZoneFor(d), d.KothMarkerSpacing).Count);
        }

        [TestMethod]
        public void EveryPoint_SitsOnTheRadius_AndIsInsideTheScoringZone()
        {
            var d = BattlegroundTunables.Defaults;
            var zone = ZoneFor(d);
            var pieces = BattlegroundZoneMarkers.Plan(Layout, zone, d.KothMarkerSpacing);

            Assert.AreEqual(16, pieces.Count);

            foreach (var p in pieces)
            {
                Assert.AreEqual(d.KothZoneRadius, Dist(p, zone), 1e-4, $"({p.X}, {p.Y}) is not on the zone edge");
                Assert.IsTrue(zone.Contains(p.X, p.Y, p.Z), $"({p.X}, {p.Y}, {p.Z}) is outside the scoring zone");
            }
        }

        [TestMethod]
        public void NeighbourSpacing_IsEqual_AndTheFirstPointIsOnPlusX()
        {
            var d = BattlegroundTunables.Defaults;
            var zone = ZoneFor(d);
            var pieces = BattlegroundZoneMarkers.Plan(Layout, zone, d.KothMarkerSpacing);

            var expected = 2 * d.KothZoneRadius * Math.Sin(Math.PI / pieces.Count);

            for (var i = 0; i < pieces.Count; i++)
            {
                var a = pieces[i];
                var b = pieces[(i + 1) % pieces.Count];
                var gap = Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

                Assert.AreEqual(expected, gap, 1e-4, $"gap {i} -> {(i + 1) % pieces.Count}");
            }

            Assert.AreEqual(zone.CenterX + d.KothZoneRadius, pieces[0].X, 1e-4, "theta 0 is +x");
            Assert.AreEqual(zone.CenterY, pieces[0].Y, 1e-4);
        }

        /// <summary>The z offset shifts every marker's height and nothing else: same count, same x/y, still on the scored edge.</summary>
        [TestMethod]
        public void ZOffset_ShiftsOnlyTheHeight()
        {
            var d = BattlegroundTunables.Defaults;
            var zone = ZoneFor(d);
            var flat = BattlegroundZoneMarkers.Plan(Layout, zone, d.KothMarkerSpacing, MarkerWcid);
            var sunk = BattlegroundZoneMarkers.Plan(Layout, zone, d.KothMarkerSpacing, MarkerWcid, -0.5);

            Assert.AreEqual(flat.Count, sunk.Count);

            for (var i = 0; i < flat.Count; i++)
            {
                Assert.AreEqual(flat[i].X, sunk[i].X, 0.0);
                Assert.AreEqual(flat[i].Y, sunk[i].Y, 0.0);
                Assert.AreEqual(flat[i].Z - 0.5f, sunk[i].Z, 1e-6, $"marker {i}");
                Assert.AreEqual(d.KothZoneRadius, Dist(sunk[i], zone), 1e-4);
            }
        }

        /// <summary>An offset beyond the zone height still places (the offset is cosmetic and is not a containment question); a NaN offset is ignored.</summary>
        [TestMethod]
        public void ZOffset_IsNotLimitedByTheZoneHeight_AndNaNIsIgnored()
        {
            var d = BattlegroundTunables.Defaults;
            var zone = ZoneFor(d);

            var high = BattlegroundZoneMarkers.Plan(Layout, zone, d.KothMarkerSpacing, MarkerWcid, d.KothZoneHeight + 2.0);
            Assert.AreEqual(16, high.Count);
            Assert.IsTrue(high.All(p => Math.Abs(Dist(p, zone) - d.KothZoneRadius) < 1e-4), "x/y still on the edge, no inset triggered");

            var nan = BattlegroundZoneMarkers.Plan(Layout, zone, d.KothMarkerSpacing, MarkerWcid, double.NaN);
            Assert.IsTrue(nan.All(p => p.Z == (float)zone.CenterZ));
        }

        /// <summary>
        /// A sunk marker is in no cell at its own point (a cell's volume starts at the floor), so the cell lookup climbs.
        /// The fake cell holds everything at or above z 0 (the shape measured on bg_016c, where 0.005 is inside and -0.3 is not).
        /// </summary>
        [TestMethod]
        public void ResolveMarkerCell_ClimbsFromBelowTheFloor_AndGivesUpWhenNothingHoldsTheColumn()
        {
            System.Func<System.Numerics.Vector3, uint?> floorAtZero = p => p.Z >= 0f ? 0x016C01BCu : (uint?)null;

            Assert.AreEqual(0x016C01BCu, ACE.Server.Pvp.LivePvpMatchSpaces.ResolveMarkerCell(floorAtZero, new System.Numerics.Vector3(55f, -35f, 0.005f)), "inside already");
            Assert.AreEqual(0x016C01BCu, ACE.Server.Pvp.LivePvpMatchSpaces.ResolveMarkerCell(floorAtZero, new System.Numerics.Vector3(55f, -35f, -0.495f)), "sunk -0.5 resolves");
            Assert.AreEqual(0x016C01BCu, ACE.Server.Pvp.LivePvpMatchSpaces.ResolveMarkerCell(floorAtZero, new System.Numerics.Vector3(55f, -35f, -1.0f)), "sunk -1.0 resolves");
            Assert.IsNull(ACE.Server.Pvp.LivePvpMatchSpaces.ResolveMarkerCell(floorAtZero, new System.Numerics.Vector3(55f, -35f, -9f)), "far below any cell");
            Assert.IsNull(ACE.Server.Pvp.LivePvpMatchSpaces.ResolveMarkerCell(p => null, new System.Numerics.Vector3(1f, 1f, 1f)), "no cell at all");
        }

        [TestMethod]
        public void ZeroOrNegativeRadius_GivesNoMarkers()
        {
            foreach (var r in new[] { 0.0, -1.0, double.NaN })
            {
                var d = BattlegroundTunables.Defaults with { KothZoneRadius = r };
                Assert.AreEqual(0, BattlegroundZoneMarkers.Plan(Layout, ZoneFor(d), d.KothMarkerSpacing).Count, $"radius {r}");
            }
        }

        [TestMethod]
        public void WcidZero_GivesNoMarkers()
        {
            var d = BattlegroundTunables.Defaults;
            var noWcid = BattlegroundMapCatalog.Bg016c with { ZoneMarkerWcid = 0 };

            Assert.AreEqual(0, BattlegroundZoneMarkers.Plan(noWcid, BattlegroundModes.KothZoneFor(noWcid, d), d.KothMarkerSpacing).Count);
            Assert.AreEqual(0, BattlegroundZoneMarkers.Plan(null, ZoneFor(d), d.KothMarkerSpacing).Count, "null layout");
        }

        [TestMethod]
        public void HugeSpacing_ClampsTo8_HugeRadius_ClampsTo32()
        {
            var d = BattlegroundTunables.Defaults;

            Assert.AreEqual(BattlegroundZoneMarkers.MinMarkers, BattlegroundZoneMarkers.Plan(Layout, ZoneFor(d), 1000).Count, "huge spacing");

            var wide = d with { KothZoneRadius = 200 };
            Assert.AreEqual(BattlegroundZoneMarkers.MaxMarkers, BattlegroundZoneMarkers.Plan(Layout, ZoneFor(wide), d.KothMarkerSpacing).Count, "huge radius");

            // A spacing under the 0.5 m floor plans as 0.5 m: R = 1 -> ceil(2 pi / 0.5) = 13.
            var small = d with { KothZoneRadius = 1 };
            Assert.AreEqual(13, BattlegroundZoneMarkers.Plan(Layout, ZoneFor(small), 0.01).Count, "spacing floor");
            Assert.AreEqual(13, BattlegroundZoneMarkers.Plan(Layout, ZoneFor(small), 0).Count, "zero spacing never divides by zero");
        }

        [TestMethod]
        public void ZWcidCellAndRotation_ComeFromTheLayout()
        {
            var d = BattlegroundTunables.Defaults;
            var pieces = BattlegroundZoneMarkers.Plan(Layout, ZoneFor(d), d.KothMarkerSpacing);
            var cell = (Layout.LandblockId << 16) | Layout.ZoneCellLow;

            foreach (var p in pieces)
            {
                Assert.AreEqual(Layout.LandblockId, p.CellId >> 16, "the CellId carries the landblock");
                Assert.AreEqual(Layout.ZoneZ, p.Z);
                Assert.AreEqual(MarkerWcid, p.Wcid);
                Assert.AreEqual(cell, p.CellId);
                Assert.AreEqual(1f, p.RotationW);
                Assert.AreEqual(0f, p.RotationZ);
            }

            var other = Layout with { ZoneMarkerWcid = 99, ZoneZ = 1.25f };
            var moved = BattlegroundZoneMarkers.Plan(other, BattlegroundModes.KothZoneFor(other, d), d.KothMarkerSpacing);
            Assert.IsTrue(moved.All(p => p.Wcid == 99 && p.Z == 1.25f), "a different layout wcid and z flow through");

            Assert.IsTrue(BattlegroundZoneMarkers.Plan(Layout, ZoneFor(d), d.KothMarkerSpacing, 7).All(p => p.Wcid == 7), "the explicit-colour overload");
        }

        /// <summary>Proves the radius tests read R: R = 9 moves every point out to 9 m and changes the count.</summary>
        [TestMethod]
        public void RadiusNine_MovesThePoints()
        {
            var d6 = BattlegroundTunables.Defaults;
            var d9 = d6 with { KothZoneRadius = 9 };

            var at6 = BattlegroundZoneMarkers.Plan(Layout, ZoneFor(d6), d6.KothMarkerSpacing);
            var zone9 = ZoneFor(d9);
            var at9 = BattlegroundZoneMarkers.Plan(Layout, zone9, d9.KothMarkerSpacing);

            // ceil(2 pi 9 / 2.5) = ceil(22.62) = 23
            Assert.AreEqual(23, at9.Count);
            Assert.AreNotEqual(at6.Count, at9.Count);
            Assert.AreNotEqual(at6[0].X, at9[0].X);

            foreach (var p in at9)
            {
                Assert.AreEqual(9.0, Dist(p, zone9), 1e-4);
                Assert.IsTrue(zone9.Contains(p.X, p.Y, p.Z));
                Assert.IsFalse(ZoneFor(d6).Contains(p.X, p.Y, p.Z), "an R = 9 point is outside the R = 6 zone");
            }
        }

        /// <summary>
        /// The zone King of the Hill SCORES (its tick handler's, which the coordinator plans the ring from) equals
        /// KothZoneFor(the map's layout, the same dials), for the defaults and for a changed radius and height.
        /// </summary>
        [TestMethod]
        public void KothMode_ScoresKothZoneFor_TheMapLayoutAndTheSameDials()
        {
            foreach (var d in new[] { BattlegroundTunables.Defaults, BattlegroundTunables.Defaults with { KothZoneRadius = 9, KothZoneHeight = 4 } })
            {
                var mode = BattlegroundModes.Koth(d);
                var layout = BattlegroundMapCatalog.Find(mode.MapPool.Single().MapKey);
                var scored = ((KothTickHandler)mode.TickHandler()).Zone;

                Assert.AreEqual(BattlegroundModes.KothZoneFor(layout, d), scored, $"radius {d.KothZoneRadius}");
                Assert.AreEqual(d.KothZoneRadius, scored.Radius);
            }
        }
    }
}
