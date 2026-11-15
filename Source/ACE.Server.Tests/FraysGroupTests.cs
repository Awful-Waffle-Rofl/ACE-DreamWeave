using System;
using System.Linq;
using System.Numerics;

using ACE.Server.Frays;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>The Sky Fray: the four group camps on the temple's floating plateau, and the group rules.</summary>
    [TestClass]
    public class FraysGroupTests
    {
        [TestInitialize]
        public void Init() => FraysSettings.ResetCacheForTest();

        private static FraysFireDef[] Sky => FraysDefs.All.Where(f => f.FrayKey == FraysDefs.VissidalSky).ToArray();

        // ---- the plateau's camps ----

        [TestMethod]
        public void Sky_FiveGroupCampsOnThePlateau()
        {
            Assert.AreEqual(5, Sky.Length, "four arms and the middle square");

            foreach (var z in Sky)
            {
                Assert.IsTrue(z.Group && z.Outdoor, z.Id);
                Assert.AreEqual(FraysDefs.VissidalSky, z.Landblock, z.Id);
                Assert.IsNotNull(z.ZoneCentre, z.Id);
                Assert.IsTrue(z.ZoneRadius > 0, z.Id);
                Assert.AreEqual(FraysDefs.PlateauAbove, z.FloorAbove, z.Id);
                Assert.IsTrue(z.SpawnPoints.Count >= 10, $"{z.Id}: {z.SpawnPoints.Count} spawn points");
                Assert.IsTrue(z.RoomName.All(c => c < 128), z.Id);
            }

            Assert.AreEqual(FraysDefs.VissidalSky, FraysDefs.FrayOfLandblock(FraysDefs.VissidalSky));
            Assert.AreEqual("Sky Fray", FraysManager.FrayName(FraysDefs.VissidalSky));
        }

        [TestMethod]
        public void Vissidal_ThreeFraysShareOneFamily()
        {
            var coast = FraysFamilies.GroupOf(FraysDefs.VissidalCoast);
            Assert.AreEqual(coast, FraysFamilies.GroupOf(FraysDefs.VissidalHeights), "east and west");
            Assert.AreEqual(coast, FraysFamilies.GroupOf(FraysDefs.VissidalSky), "and the plateau");
            Assert.AreEqual((ushort)0x002B, FraysFamilies.GroupOf(0x002B), "other Frays keep their own");
        }

        [TestMethod]
        public void Group_PacksMixAllThreeShapes()
        {
            var shapes = Enumerable.Range(0, 6).Select(FraysRules.MixedShape).ToList();
            CollectionAssert.AreEquivalent(new[] { FraysShape.Medium, FraysShape.Heavy, FraysShape.Light, FraysShape.Medium, FraysShape.Heavy, FraysShape.Light }, shapes);
            Assert.AreEqual(FraysShape.Light, FraysRules.MixedShape(-1), "a negative index still maps to a shape");
        }

        [TestMethod]
        public void Group_BoardsAreSeparateAndRollWeekly()
        {
            var rec = new FraysPlayerRecord { Week = 1, WeekBestGroupHeat = 14, WeekBestGroupRunScore = 900, BestGroupHeat = 14, BestHeat = 6 };
            rec.RollWeek(2);
            Assert.AreEqual(0, rec.WeekBestGroupHeat);
            Assert.AreEqual(0, rec.WeekBestGroupRunScore);
            Assert.AreEqual(14, rec.BestGroupHeat, "the all-time group best stays");
            Assert.AreEqual(6, rec.BestHeat, "the solo best is its own");
            Assert.IsNotNull(FraysLeaderboards.Board("group", false));
            Assert.IsNotNull(FraysLeaderboards.Board("groupscore", true));
        }

        [TestMethod]
        public void Group_RatingIsItsOwn()
        {
            var rec = new FraysPlayerRecord { Rating = 12, GroupRating = 4 };
            Assert.AreEqual(12, rec.RatingFor(false));
            Assert.AreEqual(4, rec.RatingFor(true));
            Assert.IsNotNull(FraysLeaderboards.Board("grouprating", false));
        }

        [TestMethod]
        public void Sky_WingFiresMirrorTheNorthOne()
        {
            var mid = FraysDefs.PlateauMiddle;
            var north = FraysDefs.Find("vs4").FirePos;
            var outNorth = north.Y - mid.Y;

            foreach (var id in new[] { "vs1", "vs2", "vs3" })
            {
                var f = FraysDefs.Find(id).FirePos;
                Assert.AreEqual(outNorth, Vector2.Distance(new Vector2(f.X, f.Y), mid), 0.01f, id);
                Assert.AreEqual(north.Z, f.Z, 0.01f, id);
            }

            Assert.IsTrue(FraysDefs.Find("vs5").BigGroup, "the middle needs the bigger fellowship");
            Assert.IsFalse(FraysDefs.Find("vs1").BigGroup);
            Assert.AreEqual(5, FraysSettings.Int(FraysSettings.GroupMinSizeMiddle));
        }

        [TestMethod]
        public void Journal_PagesStayUnderTheBookLimit()
        {
            var lines = Enumerable.Range(0, 200).Select(i => $"  line {i} of a long Codex section").ToList();
            lines.Insert(0, "--- Families fought --- 3 of 61 complete");
            var pages = FraysJournal.Paginate(lines);

            Assert.IsTrue(pages.Count > 1);
            Assert.IsTrue(pages.All(p => p.Length <= FraysJournal.PageChars), "every page fits");
            Assert.IsTrue(pages.Skip(1).All(p => p.StartsWith("--- Families fought") && p.Contains("(continued)")), "later pages keep the heading");
            Assert.AreEqual(201, pages.Sum(p => p.Split('\n').Length) - (pages.Count - 1), "no line lost");
        }

        [TestMethod]
        public void Sky_EachArmHasAWispLineAcrossIt()
        {
            var north = FraysDefs.Find("vs4").WispLine.Value;
            Assert.AreEqual(140.7f, north.A.Y, 0.01f, "a measured spot, 32.7 m north of the middle");
            Assert.AreEqual(140.7f, north.B.Y, 0.01f);
            Assert.AreEqual(56f, Math.Abs(north.B.X - north.A.X), 0.01f, "across the arm");

            foreach (var id in new[] { "vs1", "vs2", "vs3", "vs4" })
            {
                var line = FraysDefs.Find(id).WispLine.Value;
                var mid = (line.A + line.B) / 2;
                Assert.AreEqual(FraysDefs.PlateauWispLineOut, System.Numerics.Vector2.Distance(mid, FraysDefs.PlateauMiddle), 0.01f, id);
                Assert.IsTrue(FraysGroup.LineSpots(line.A, line.B, 2).Count > 20, id);
            }

            Assert.IsNull(FraysDefs.Find("vs5").WispLine, "the middle square keeps its circle");
        }

        [TestMethod]
        public void Sky_SpotOverTheTileEdgeIsInTheFiresFrame()
        {
            // A spot by the north spire: 0xD5DB, 0.27 m over the edge of the plateau's 0xD5DA.
            Assert.IsTrue(FraysGroup.TryLocal(0xD5DA, 0xD5DB, new Vector3(107.97f, 0.27f, 116f), out var at));
            Assert.AreEqual(192.27f, at.Y, 0.01f);
            Assert.AreEqual(107.97f, at.X, 0.01f);
            Assert.IsTrue(FraysGroup.BeyondLine(FraysDefs.Find("vs4").WispLine.Value, FraysDefs.PlateauMiddle, at), "in the north arm");

            Assert.IsTrue(FraysGroup.TryLocal(0xD5DA, 0xD4DA, new Vector3(190f, 50f, 116f), out at));
            Assert.AreEqual(-2f, at.X, 0.01f, "west neighbour");
            Assert.IsFalse(FraysGroup.TryLocal(0xD5DA, 0xD5DC, new Vector3(10f, 10f, 116f), out _), "two tiles away");
        }

        [TestMethod]
        public void Sky_ArmZoneIsEverythingPastItsLine()
        {
            foreach (var id in new[] { "vs1", "vs2", "vs3", "vs4" })
            {
                var z = FraysDefs.Find(id);
                var line = z.WispLine.Value;

                foreach (var p in z.SpawnPoints.Select(s => s.Pos).Append(z.FirePos))
                    Assert.IsTrue(FraysGroup.BeyondLine(line, FraysDefs.PlateauMiddle, p), $"{id}: {p} on the wrong side of its line");

                Assert.IsFalse(FraysGroup.BeyondLine(line, FraysDefs.PlateauMiddle, new Vector3(108, 108, 116)), $"{id}: the middle is not in an arm");
            }

            // North arm, out at the side past the line but outside the old 30 m circle: in the zone now.
            var north = FraysDefs.Find("vs4");
            var side = new Vector3(84, 150, 116);
            Assert.IsFalse(FraysGroup.InCircle(side, north.ZoneCentre.Value, north.ZoneRadius));
            Assert.IsTrue(FraysGroup.BeyondLine(north.WispLine.Value, FraysDefs.PlateauMiddle, side));
            Assert.IsFalse(FraysGroup.BeyondLine(north.WispLine.Value, FraysDefs.PlateauMiddle, new Vector3(108, 139, 116)), "just short of the line");
        }

        [TestMethod]
        public void Sky_PointsSitOnThePlateauInsideTheirArm()
        {
            foreach (var z in Sky)
            {
                var centre = z.ZoneCentre.Value;

                foreach (var p in z.SpawnPoints.Append(new FraysSpawnPoint(z.FireCell, z.FirePos.X, z.FirePos.Y, z.FirePos.Z)))
                {
                    Assert.IsTrue(p.Pos.X > 0 && p.Pos.X < 192 && p.Pos.Y > 0 && p.Pos.Y < 192, $"{z.Id}: {p.Pos} outside the landblock");
                    Assert.AreEqual(FraysRules.OutdoorCell(true, 0, p.Pos), p.Cell, $"{z.Id}: cell id of {p.Pos}");
                    Assert.AreEqual(FraysDefs.PlateauFloor, p.Pos.Z, 1e-3, $"{z.Id}: on the plateau floor");
                    Assert.IsTrue(FraysGroup.InCircle(p.Pos, centre, z.ZoneRadius), $"{z.Id}: {p.Pos} outside its zone");
                }

                // Spread out: no two points on top of each other, none on the fire.
                var pts = z.SpawnPoints.Select(s => s.Pos).ToList();

                for (var i = 0; i < pts.Count; i++)
                {
                    Assert.IsTrue(Vector3.Distance(pts[i], z.FirePos) >= 10, $"{z.Id}: point {i} on the fire");

                    for (var j = i + 1; j < pts.Count; j++)
                        Assert.IsTrue(Vector3.Distance(pts[i], pts[j]) >= 8, $"{z.Id}: points {i} and {j} too close");
                }
            }
        }

        [TestMethod]
        public void Sky_TheArrivalStaysClear()
        {
            // The Sky Fray portal lands you at (109.7, 110) on the middle square (x and y 76 to 140); the return portal
            // stands about 4 m south of it. Only the middle camp (vs5) holds the square.
            var landing = new Vector3(109.7f, 109.95f, FraysDefs.PlateauFloor);
            var portal = new Vector3(109.7f, 106f, FraysDefs.PlateauFloor);
            var middle = FraysDefs.Find("vs5");

            Assert.IsTrue(Vector3.Distance(middle.FirePos, landing) >= 6, "nobody lands in the fire");
            Assert.IsTrue(Vector3.Distance(middle.FirePos, portal) >= 6, "the fire keeps off the return portal");

            foreach (var p in middle.SpawnPoints)
            {
                Assert.IsTrue(Vector3.Distance(p.Pos, landing) >= 14, $"vs5: point {p.Pos} too near the arrival");
                Assert.IsTrue(Vector3.Distance(p.Pos, portal) >= 10, $"vs5: point {p.Pos} too near the return portal");
            }

            foreach (var z in Sky.Where(z => z.Id != "vs5"))
            {
                Assert.IsFalse(FraysGroup.InCircle(landing, z.ZoneCentre.Value, z.ZoneRadius), $"{z.Id}: its zone reaches the landing");

                foreach (var p in z.SpawnPoints)
                    Assert.IsFalse(p.Pos.X > 74 && p.Pos.X < 142 && p.Pos.Y > 74 && p.Pos.Y < 142, $"{z.Id}: point {p.Pos} on the middle square");
            }

            // The arms' zones never overlap.
            for (var i = 0; i < Sky.Length; i++)
                for (var j = i + 1; j < Sky.Length; j++)
                    Assert.IsTrue(Vector2.Distance(Sky[i].ZoneCentre.Value, Sky[j].ZoneCentre.Value) > Sky[i].ZoneRadius + Sky[j].ZoneRadius, $"{Sky[i].Id} and {Sky[j].Id} overlap");

            var spot = FraysFrayWide.GreatSnarlSpots[FraysDefs.VissidalSky];
            Assert.AreEqual(FraysDefs.PlateauFloor, spot.Pos.Z, 1e-3, "the Great Snarl lands on the plateau, not the sea floor");
            Assert.AreEqual(FraysRules.OutdoorCell(true, 0, spot.Pos), spot.Cell);
            Assert.IsFalse(Sky.Any(z => FraysGroup.InCircle(spot.Pos, z.ZoneCentre.Value, z.ZoneRadius)), "the Great Snarl walks in outside every camp");
        }

        [TestMethod]
        public void Sky_IsInTheDefaultFrayOrder()
        {
            var order = FraysSettings.All.First(s => s.Key == FraysSettings.FrayOrder).TextDefault;
            Assert.IsTrue(order.Split(',').Contains("D5DA"), order);
        }

        // ---- geometry round a zone's own centre ----

        [TestMethod]
        public void Geometry_CircleRoundItsOwnCentre()
        {
            var c = new Vector2(36, 108);
            Assert.IsTrue(FraysGroup.InCircle(new Vector3(36, 137, 0), c, 30));
            Assert.IsFalse(FraysGroup.InCircle(new Vector3(70, 108, 0), c, 30));

            var pulled = FraysGroup.InsideCircle(new Vector3(36, 160, 116), c, 28);
            Assert.AreEqual(28, Vector2.Distance(new Vector2(pulled.X, pulled.Y), c), 0.01);
            Assert.AreEqual(116, pulled.Z, 1e-6, "height kept");

            Assert.AreEqual(0, FraysGroup.Quadrant(new Vector3(30, 100, 0), c));
            Assert.AreEqual(3, FraysGroup.Quadrant(new Vector3(40, 120, 0), c));

            var ring = FraysGroup.RingSpots(new Vector2(172, 108), 30, 6);
            Assert.IsTrue(ring.Count > 8);
            Assert.IsTrue(ring.All(s => s.X < 192), "a ring spot past the landblock edge is left out");

            foreach (var (x, y) in ring)
                Assert.AreEqual(30, Vector2.Distance(new Vector2(x, y), new Vector2(172, 108)), 0.01);

            Assert.IsTrue(FraysGroup.OnFloor(null, 0), "no raised floor: anywhere");
            Assert.IsTrue(FraysGroup.OnFloor(100, 116));
            Assert.IsFalse(FraysGroup.OnFloor(100, 2), "the village below is not the plateau");

            var island = FraysDefs.Find("vc1");
            Assert.AreEqual(new Vector2(FraysRules.ZoneCentre, FraysRules.ZoneCentre), FraysGroup.CentreOf(island), "island zones keep the landblock's middle");
            Assert.AreEqual(90, FraysGroup.RadiusOf(island, 90));
            Assert.AreEqual(FraysDefs.PlateauCampRadius, FraysGroup.RadiusOf(FraysDefs.Find("vs1"), 90));
        }

        // ---- the group rules ----

        [TestMethod]
        public void Group_NeedsThreePlayersByDefault()
        {
            var min = FraysSettings.Int(FraysSettings.GroupMinSize);
            Assert.AreEqual(3, min);

            Assert.IsTrue(FraysGroup.MeetsMinSize(3, 3, min, true));
            Assert.IsFalse(FraysGroup.MeetsMinSize(2, 2, min, true), "two is not a group");
            Assert.IsFalse(FraysGroup.MeetsMinSize(3, 1, min, true), "three characters of one player are not a group");
            Assert.IsTrue(FraysGroup.MeetsMinSize(3, 1, min, false), "with one fire per player off, characters count");
        }

        [TestMethod]
        public void Group_ScalesWithMembersPresent()
        {
            Assert.AreEqual(1, FraysGroup.HealthMult(1, 0.5), 1e-9, "one member: as solo");
            Assert.AreEqual(3, FraysGroup.HealthMult(5, 0.5), 1e-9);
            Assert.AreEqual(0, FraysGroup.PackExtra(1, 1));
            Assert.AreEqual(4, FraysGroup.PackExtra(5, 1));
            Assert.AreEqual(1, FraysGroup.PackExtra(3, 0.5), "rounded down");
            Assert.AreEqual(2, FraysGroup.RateMult(3, 0.5), 1e-9);
            Assert.AreEqual(1, FraysGroup.RateMult(0, 0.5), 1e-9, "nobody present: never below solo");
            Assert.AreEqual(0, FraysGroup.DeathDrop(-3));
        }

        [TestMethod]
        public void Group_StartsAtTheLowestMembersStart()
        {
            Assert.AreEqual(4, FraysGroup.StartLevel(new[] { 9, 4, 12 }));
            Assert.AreEqual(0, FraysGroup.StartLevel(Array.Empty<int>()));
        }

        [TestMethod]
        public void Group_SettingsHaveSaneDefaults()
        {
            Assert.AreEqual(0.5, FraysSettings.Number(FraysSettings.GroupHealthPerMember), 1e-9);
            Assert.AreEqual(1, FraysSettings.Number(FraysSettings.GroupPackPerMember), 1e-9);
            Assert.AreEqual(0.5, FraysSettings.Number(FraysSettings.GroupRatePerMember), 1e-9);
            Assert.AreEqual(2, FraysSettings.Int(FraysSettings.GroupDeathDrop), "a death costs the group 2 Heat levels, like a solo death");
        }
    }
}
