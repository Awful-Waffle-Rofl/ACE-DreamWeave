using System.Linq;
using System.Numerics;

using ACE.Server.HazardMechanics;
using ACE.Server.Pinnacles;
using ACE.Server.Pinnacles.Harbinger;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.Pinnacles
{
    /// <summary>
    /// The per-run region a Pinnacle hazard gets (PinnacleHazardRegions.Resolve, the body of LivePinnacleWorld.StartHazard's
    /// region choice) and the four Harbinger alcove presets (Docs/Knots/HARBINGER-ROUND2-PLAN.md U1). Pure: no dats, no
    /// settings. The dat proof of the alcove geometry is HazardMechanics.HarbingerAlcoveRegionTests.
    /// </summary>
    [TestClass]
    public class PinnacleHazardRegionsTests
    {
        private const uint Instance = 0x7F000123;

        private static readonly string[] AlcoveNames =
        {
            HazardRegionPresets.HarbingerAlcoveWestName,
            HazardRegionPresets.HarbingerAlcoveNorthName,
            HazardRegionPresets.HarbingerAlcoveSouthName,
            HazardRegionPresets.HarbingerAlcoveEastName,
        };

        // ================================================================ Resolve

        [TestMethod]
        public void NoOptions_OrNoRegionChoice_IsTheFightPreset()
        {
            foreach (var options in new[] { null, new PinnacleHazardOptions(), new PinnacleHazardOptions(new PinnacleHitSlow(0.3, 1.0, 2.0)) })
            {
                var region = PinnacleHazardRegions.Resolve(options, "harbinger", Instance, out var error);

                Assert.IsNull(error);
                Assert.AreEqual("harbinger", region.Name);
                Assert.AreEqual(Instance, region.Instance, "stamped with the fight's instance");
            }
        }

        [TestMethod]
        public void RegionOption_PicksThatPreset_InsteadOfTheFightPreset()
        {
            var region = PinnacleHazardRegions.Resolve(new PinnacleHazardOptions(Region: HazardRegionPresets.HarbingerAlcoveSouthName), "harbinger", Instance, out var error);

            Assert.IsNull(error);
            Assert.AreEqual(HazardRegionPresets.HarbingerAlcoveSouthName, region.Name);
            Assert.AreEqual(Instance, region.Instance);
            Assert.AreEqual(HazardShape.Circle, region.Shape);
            Assert.AreEqual(20f, region.CenterX);
            Assert.AreEqual(-40f, region.CenterY);
        }

        [TestMethod]
        public void AreaOption_BuildsARectInTheFightLandblock()
        {
            var area = new PinnacleHazardArea(18.5f, 21f, -1f, 1.5f, 0f, CeilingZ: 9f, BandBelow: 0.5f, BandAbove: 4f);

            var region = PinnacleHazardRegions.Resolve(new PinnacleHazardOptions(Area: area), "harbinger", Instance, out var error);

            Assert.IsNull(error);
            Assert.AreEqual(PinnacleHazardRegions.AreaName, region.Name);
            Assert.AreEqual(HazardShape.Rect, region.Shape);
            Assert.AreEqual((ushort)0x00BC, region.Landblock, "the fight preset's landblock");
            Assert.AreEqual(Instance, region.Instance);
            Assert.IsFalse(region.Outdoor);
            Assert.AreEqual((18.5f, 21f, -1f, 1.5f), (region.MinX, region.MaxX, region.MinY, region.MaxY));
            Assert.AreEqual(HazardGroundMode.Plane, region.GroundMode);
            Assert.AreEqual(0f, region.GroundPlaneZ);
            Assert.AreEqual(9f, region.CeilingZ);
            Assert.AreEqual(0.5f, region.BandBelow);
            Assert.AreEqual(4f, region.BandAbove);
            Assert.AreEqual(HazardRegionPresets.PinnacleWispOffset, region.HazardOffset, "the fight preset's hazard offset");
            Assert.IsNull(region.Validate(), "a usable region");
        }

        [TestMethod]
        public void AreaOption_DefaultsToTheAdHocBand_AndNoCeiling()
        {
            var region = PinnacleHazardRegions.Resolve(new PinnacleHazardOptions(Area: new PinnacleHazardArea(19f, 21f, -1f, 1f, 0f)), "harbinger", Instance, out _);

            Assert.IsNull(region.CeilingZ);
            Assert.AreEqual(HazardDefaults.BandBelow, region.BandBelow);
            Assert.AreEqual(HazardDefaults.BandAbove, region.BandAbove);
        }

        [TestMethod]
        public void Refusals_NameTheProblem()
        {
            Assert.IsNull(PinnacleHazardRegions.Resolve(new PinnacleHazardOptions(Region: "harbinger-alcove-n", Area: new PinnacleHazardArea(0, 1, 0, 1, 0)), "harbinger", Instance, out var both));
            StringAssert.Contains(both, "both");

            Assert.IsNull(PinnacleHazardRegions.Resolve(new PinnacleHazardOptions(Region: "no-such-preset"), "harbinger", Instance, out var missing));
            StringAssert.Contains(missing, "no-such-preset");

            Assert.IsNull(PinnacleHazardRegions.Resolve(new PinnacleHazardOptions(Region: HazardRegionPresets.MatronName), "harbinger", Instance, out var elsewhere));
            StringAssert.Contains(elsewhere, "0x586B", "a preset on another landblock is refused");

            Assert.IsNull(PinnacleHazardRegions.Resolve(null, "no-fight-preset", Instance, out var noFight));
            StringAssert.Contains(noFight, "no-fight-preset");

            Assert.IsNull(PinnacleHazardRegions.Resolve(new PinnacleHazardOptions(Area: new PinnacleHazardArea(0, 1, 0, 1, 0)), "no-fight-preset", Instance, out var areaNoFight),
                "an area needs the fight preset for its landblock");
            StringAssert.Contains(areaNoFight, "no-fight-preset");
        }

        // ================================================================ alcove presets

        [TestMethod]
        public void AlcovePresets_AreRegistered_AndHarbingerStaysTheLandblockDefault()
        {
            var names = HazardRegionPresets.Names.ToArray();

            foreach (var n in AlcoveNames)
                CollectionAssert.Contains(names, n);

            Assert.AreEqual("harbinger", HazardRegionPresets.ForLandblock(0x00BC), "the alcove names sort after harbinger");
            Assert.AreEqual(HazardRegionPresets.PinnacleName, HazardRegionPresets.ForLandblock(0x0069));
            Assert.AreEqual(HazardRegionPresets.MatronName, HazardRegionPresets.ForLandblock(0x586B));
        }

        [TestMethod]
        public void EachAlcovePreset_IsACircleOnItsAlcove_WithTheMeasuredFloorAndCeiling()
        {
            foreach (var n in AlcoveNames)
            {
                Assert.IsTrue(HazardRegionPresets.TryGet(n, Instance, out var r), n);
                Assert.IsNull(r.Validate(), $"{n}: {r.Validate()}");

                var alcove = HarbingerData.Alcoves.Single(a => a.Name == n.Substring(n.Length - 1).ToUpperInvariant());

                Assert.AreEqual((ushort)HarbingerData.Landblock, r.Landblock, n);
                Assert.AreEqual(HazardShape.Circle, r.Shape, n);
                Assert.AreEqual(alcove.Centre.X, r.CenterX, 1e-4, n);
                Assert.AreEqual(alcove.Centre.Y, r.CenterY, 1e-4, n);
                Assert.AreEqual(4.0f, r.Radius, n);
                Assert.AreEqual(HarbingerLayout.AlcoveZ, r.GroundPlaneZ, n);
                Assert.AreEqual(9.0f, r.CeilingZ, n);
                Assert.AreEqual(0.5f, r.BandBelow, n);
                Assert.AreEqual(9.0f, r.BandAbove, n);

                // every point the alcove's actors stand on is inside the region
                foreach (var p in new[] { alcove.Stand, alcove.Crystal, alcove.Guards[0], alcove.Guards[1] })
                    Assert.IsTrue(r.Contains(p.X, p.Y), $"{n}: ({p.X}, {p.Y})");

                // placement stays in the 4.0 circle; a player counts as inside out to 5.3 (no safe ring along the wall)
                Assert.AreEqual(1.3f, r.PlayerMargin, 1e-5f, n);
                var wall = alcove.Centre + 4.8f * Vector2.Normalize(new Vector2(alcove.Outward.Y, -alcove.Outward.X));
                Assert.IsFalse(r.Contains(wall.X, wall.Y), $"{n}: nothing is placed at 4.8 m");
                Assert.IsFalse(r.IsPlaceable(wall.X, wall.Y), n);
                Assert.IsTrue(r.ContainsPlayerXY(wall.X, wall.Y), $"{n}: a player at the wall, 4.8 m out, is inside");
                Assert.IsTrue(r.ContainsPlayer(new Vector3(wall.X, wall.Y, 0f)), n);
                Assert.IsFalse(r.ContainsPlayerXY(alcove.Centre.X + 5.4f, alcove.Centre.Y), $"{n}: past 5.3 is out");

                // and it never reaches the chamber, whose preset band (-36.5..-16) is 16 m below the alcove band
                Assert.IsTrue(r.GroundPlaneZ - r.BandBelow > -16.0f, n);
            }
        }

        // ================================================================ HazardRegion.PlayerMargin

        [TestMethod]
        public void PlayerMargin_GrowsOnlyThePlayerTest()
        {
            var circle = HazardRegion.Circle("c", 0x00BC, 1, false, 0f, 0f, 4f, HazardGroundMode.Plane, 0f, null, 0.5f, null, 0.5f, 3f, playerMargin: 1f);
            Assert.IsFalse(circle.Contains(4.5f, 0f));
            Assert.IsTrue(circle.ContainsPlayerXY(4.5f, 0f));
            Assert.IsTrue(circle.ContainsPlayerXY(5f, 0f));
            Assert.IsFalse(circle.ContainsPlayerXY(5.01f, 0f));
            Assert.AreEqual(1f, circle.WithInstance(9).PlayerMargin, "WithInstance keeps it");
            StringAssert.Contains(circle.Describe(), "player+1");

            var none = HazardRegion.Circle("c", 0x00BC, 1, false, 0f, 0f, 4f, HazardGroundMode.Plane, 0f, null, 0.5f, null, 0.5f, 3f);
            Assert.AreEqual(0f, none.PlayerMargin, "default 0");
            Assert.IsFalse(none.ContainsPlayerXY(4.5f, 0f), "margin 0: the player shape is the placement shape");
            Assert.IsFalse(none.Describe().Contains("player+"), "describe unchanged for every existing region");

            var bad = HazardRegion.Circle("c", 0x00BC, 1, false, 0f, 0f, 4f, HazardGroundMode.Plane, 0f, null, 0.5f, null, 0.5f, 3f, playerMargin: -1f);
            StringAssert.Contains(bad.Validate(), "player margin");

            // every existing preset keeps margin 0
            foreach (var name in new[] { HazardRegionPresets.PinnacleName, HazardRegionPresets.MatronName, HazardRegionPresets.HarbingerName })
            {
                HazardRegionPresets.TryGet(name, 0, out var r);
                Assert.AreEqual(0f, r.PlayerMargin, name);
            }
        }

        [TestMethod]
        public void NoAlcove_HasAnExclusion_Round4DroppedTheFireDoorwayLane()
        {
            foreach (var n in AlcoveNames)
            {
                HazardRegionPresets.TryGet(n, Instance, out var r);
                Assert.AreEqual(0, r.Exclusions.Count, n);
            }

            // the stand point, the crystal and the old lane's edge are all placeable in the fire alcove now
            var fire = HarbingerData.AlcoveOf(HarbingerElement.Fire);
            HazardRegionPresets.TryGet(HazardRegionPresets.HarbingerAlcoveNorthName, Instance, out var north);
            Assert.IsFalse(north.InExclusion(fire.Stand.X, fire.Stand.Y));
            Assert.IsFalse(north.InExclusion(fire.Crystal.X, fire.Crystal.Y));
            Assert.IsFalse(north.InExclusion(fire.Centre.X + 1.6f, fire.Centre.Y));
        }
    }
}
