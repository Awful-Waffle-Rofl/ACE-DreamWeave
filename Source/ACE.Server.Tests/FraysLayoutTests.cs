using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ACE.Server.Frays;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>Solo camp layouts (FraysLayout): fixed roles, 2 rich points, 1 shrine, packs spread apart.</summary>
    [TestClass]
    public class FraysLayoutTests
    {
        public TestContext TestContext { get; set; }

        private Func<string, string> previousSource;
        private Dictionary<string, string> values;

        [TestInitialize]
        public void Init()
        {
            previousSource = FraysSettings.StringSource;
            values = new Dictionary<string, string>();
            FraysSettings.StringSource = p => values.TryGetValue(p, out var v) ? v : null;
            FraysSettings.ResetCacheForTest();
        }

        [TestCleanup]
        public void Cleanup()
        {
            FraysSettings.StringSource = previousSource;
            FraysSettings.ResetCacheForTest();
        }

        private static double Flat(Vector3 a, Vector3 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

        private static IEnumerable<FraysFireDef> SoloCamps => FraysDefs.All.Where(f => f.Outdoor && !f.Group);

        /// <summary>A camp's layout as FraysFire.Layout builds it with the default settings.</summary>
        private static FraysCampLayout LayoutOf(FraysFireDef d)
        {
            var radius = FraysSettings.Number(FraysSettings.ZoneRadius) - 18;
            var gap = FraysSettings.Number(FraysSettings.LayoutGapAggro) * FraysSettings.Number(FraysSettings.AggroRange);
            var points = d.SpawnPoints.Select(p => FraysRules.InsideCircle(p.Pos, radius)).ToList();
            return FraysLayout.Build(d.Id, points, d.FirePos, new Vector2(FraysRules.ZoneCentre, FraysRules.ZoneCentre), radius, gap,
                FraysSettings.Int(FraysSettings.RichPoints), FraysSettings.Toggle(FraysSettings.ShrineEnabled),
                FraysSettings.Int(FraysSettings.LayoutPoints), FraysSettings.Toggle(FraysSettings.LayoutEdges));
        }

        private static readonly Vector2 Centre = new Vector2(FraysRules.ZoneCentre, FraysRules.ZoneCentre);

        [TestMethod]
        public void EveryPoint_StandsFourAggroRangesFromEveryOther()
        {
            var gap = FraysSettings.Number(FraysSettings.LayoutGapAggro) * FraysSettings.Number(FraysSettings.AggroRange);
            Assert.AreEqual(40, gap, 1e-9, "4 x the 10 m aggro range (clear lanes between packs)");

            foreach (var d in SoloCamps)
            {
                var l = LayoutOf(d);
                var used = Enumerable.Range(0, l.Positions.Count).Where(i => l.RoleOf(i) != FraysPointRole.Off).ToList();

                for (var a = 0; a < used.Count; a++)
                    for (var b = a + 1; b < used.Count; b++)
                        Assert.IsTrue(Flat(l.Positions[used[a]], l.Positions[used[b]]) >= gap - 1e-3, $"{d.Id}: points {used[a]} and {used[b]} too close");

                foreach (var i in used)
                    Assert.IsTrue(Flat(l.Positions[i], new Vector3(96, 96, 0)) <= 72 + 0.1, $"{d.Id}: point {i} outside the zone");

                foreach (var i in used)
                    Assert.IsTrue(Flat(l.Positions[i], d.FirePos) >= gap - 1e-3, $"{d.Id}: point {i} pulls whoever stands at the fire");

                var off = l.Roles.Count(r => r == FraysPointRole.Off);
                var nearest = used.Min(a => used.Where(b => b != a).Min(b => Flat(l.Positions[a], l.Positions[b])));
                var byFire = used.Min(a => Flat(l.Positions[a], d.FirePos));
                TestContext.WriteLine($"{d.Id}: {used.Count} points in use, {off} off, closest pair {nearest:F1} m, closest to the fire {byFire:F1} m");
                Assert.IsTrue(used.Count >= 8 && used.Count <= FraysSettings.Int(FraysSettings.LayoutPoints), $"{d.Id}: {used.Count} points in use");
            }
        }

        [TestMethod]
        public void EveryCamp_HasFourRichPointsAndOneShrine_ClearOfTheFire()
        {
            foreach (var d in SoloCamps)
            {
                var l = LayoutOf(d);
                // Two Gilded brute and two Gilded swarm points along the route.
                Assert.AreEqual(4, l.Rich.Count, d.Id);
                Assert.AreEqual(4, l.Roles.Count(r => r == FraysPointRole.Rich), d.Id);
                Assert.AreEqual(1, l.Roles.Count(r => r == FraysPointRole.Shrine), d.Id);
                Assert.IsTrue(l.Shrine >= 0 && !l.Rich.Contains(l.Shrine), d.Id);

                foreach (var i in l.Rich.Append(l.Shrine))
                    Assert.IsTrue(Flat(l.Positions[i], d.FirePos) >= 25, $"{d.Id}: special point {i} by the fire");

                Assert.IsTrue(Flat(l.Positions[l.Rich[0]], l.Positions[l.Rich[1]]) >= 50, $"{d.Id}: the rich points sit together");

                // The first two Gilded and the shrine sit on the rim, on different edges; a third and fourth Gilded go
                // wherever they fit two gaps from the others.
                foreach (var i in l.Rich.Take(2).Append(l.Shrine))
                    Assert.IsTrue(Flat(l.Positions[i], new Vector3(Centre, 0)) >= FraysLayout.RimShare * 72 - 1e-3, $"{d.Id}: special point {i} not on the rim");

                var richApart = FraysLayout.Turn(l.Positions[l.Rich[0]], l.Positions[l.Rich[1]], Centre);
                var shrineApart = l.Rich.Min(r => FraysLayout.Turn(l.Positions[r], l.Positions[l.Shrine], Centre));
                TestContext.WriteLine($"{d.Id}: Gilded {richApart:F0} degrees apart, the shrine {shrineApart:F0} from the nearer");
                Assert.IsTrue(richApart >= FraysLayout.RichApartDegrees - 1e-6, $"{d.Id}: Gilded only {richApart:F0} degrees apart");
                // With four Gilded points the rim is shared: the shrine stays at least 30 degrees from each.
                Assert.IsTrue(shrineApart >= 30, $"{d.Id}: the shrine only {shrineApart:F0} degrees from a Gilded point");
            }
        }

        [TestMethod]
        public void Thin_KeepsTheWidestSpread()
        {
            var pts = new List<Vector3> { new Vector3(10, 0, 0), new Vector3(11, 0, 0), new Vector3(-10, 0, 0), new Vector3(0, 10, 0), new Vector3(0, -10, 0) };
            var off = FraysLayout.Thin(pts, pts.Select(_ => true).ToList(), new Vector3(0, 0, 0), 4);
            CollectionAssert.AreEqual(new List<int> { 0 }, off, "one of the twins goes (the one nearer the fire)");
            Assert.AreEqual(0, FraysLayout.Thin(pts, pts.Select(_ => true).ToList(), new Vector3(0, 0, 0), 0).Count, "0 keeps all");
            Assert.AreEqual(90, FraysLayout.Turn(pts[0], pts[3], new Vector2(0, 0)), 1e-3);
            Assert.AreEqual(180, FraysLayout.Turn(pts[3], pts[4], new Vector2(0, 0)), 1e-3);
        }

        [TestMethod]
        public void RestScale_CountsOnlyPointsThatFieldPacks()
        {
            // Leaving out a point means the rest fill the hour: their rests shrink.
            var all = FraysRules.RestScale(10, FraysShape.Medium, 2, false, 600);
            var nine = FraysRules.RestScale(Enumerable.Range(0, 10).Where(i => i != 4).ToList(), FraysShape.Medium, 2, false, 600);
            Assert.IsTrue(nine < all, $"{nine} vs {all}");
            Assert.AreEqual(all, FraysRules.RestScale(Enumerable.Range(0, 10).ToList(), FraysShape.Medium, 2, false, 600), 1e-9);
            Assert.AreEqual(0.85, FraysSettings.Number(FraysSettings.LayoutRestMult), 1e-9);
            Assert.AreEqual(2, FraysSettings.Int(FraysSettings.LayoutPackBonus));
        }

        [TestMethod]
        public void Layout_IsTheSameEveryTime_AndDiffersCampToCamp()
        {
            var shapes = new HashSet<string>();

            foreach (var d in SoloCamps)
            {
                var a = LayoutOf(d);
                var b = LayoutOf(d);
                CollectionAssert.AreEqual(a.Roles.ToList(), b.Roles.ToList(), d.Id);
                CollectionAssert.AreEqual(a.Positions.ToList(), b.Positions.ToList(), d.Id);
                shapes.Add($"{string.Join(",", a.Rich)}/{a.Shrine}/{FraysLayout.FixedShift(d.Id) % 30}");
            }

            Assert.IsTrue(shapes.Count >= SoloCamps.Count() - 2, $"only {shapes.Count} distinct layouts");
            Assert.AreEqual(FraysLayout.FixedShift("vc1"), FraysRules.WeekShift("vc1", 0), "the fixed turn never moves with the week");
        }

        [TestMethod]
        public void Spread_LeavesClearPointsAlone_AndMovesOrDropsCrowdedOnes()
        {
            var pts = new List<Vector3> { new Vector3(96, 96, 0), new Vector3(150, 96, 0), new Vector3(100, 96, 0) };
            var s = FraysLayout.Spread(pts, new Vector2(96, 96), 72, 25);
            Assert.AreEqual(pts[0], s[0]);
            Assert.AreEqual(pts[1], s[1]);
            Assert.IsNotNull(s[2]);
            Assert.IsTrue(Flat(s[2].Value, pts[0]) >= 25 && Flat(s[2].Value, pts[1]) >= 25);

            // a tiny zone with no room left: the crowded point is switched off
            var tiny = FraysLayout.Spread(new List<Vector3> { new Vector3(0, 0, 0), new Vector3(1, 0, 0) }, new Vector2(0, 0), 5, 25);
            Assert.IsNull(tiny[1]);
        }

        [TestMethod]
        public void RichPoints_RestTheirOwnTime_AndPayThreeTimes()
        {
            Assert.AreEqual(210, FraysRules.PointRest(FraysPointKind.Rich, false), 1e-9);
            Assert.AreEqual(3, FraysSettings.Number(FraysSettings.RichMult), 1e-9);
            Assert.AreEqual(2, FraysSettings.Number(FraysSettings.RichLootMult), 1e-9);
            Assert.AreEqual(15, FraysSettings.Number(FraysSettings.RichWarnSeconds), 1e-9);
            Assert.AreEqual("Gilded pack", FraysRules.PackName(FraysPointKind.Rich, 0, FraysShape.Medium));
        }

        [TestMethod]
        public void PackLink_OnByDefault_AndOnlyLinksOnePack()
        {
            Assert.IsTrue(FraysSettings.Toggle(FraysSettings.PackLink));

            var a = new FraysCreatureTag(null, 7, 3, 4, 1);
            var mate = new FraysCreatureTag(null, 7, 3, 4, 1);
            var neighbour = new FraysCreatureTag(null, 7, 3, 5, 1);
            var otherCamper = new FraysCreatureTag(null, 8, 3, 4, 1);
            var snarl = new FraysCreatureTag(null, 7, 3, 4, 1) { Snarl = true };

            Assert.IsTrue(FraysFire.SamePack(a, mate), "same camper, same point");
            Assert.IsFalse(FraysFire.SamePack(a, neighbour), "a neighbouring point never comes");
            Assert.IsFalse(FraysFire.SamePack(a, otherCamper));
            Assert.IsFalse(FraysFire.SamePack(a, snarl), "Snarls keep their own rules");
            Assert.IsFalse(FraysFire.SamePack(a, null));
        }

        [TestMethod]
        public void ShrineBuff_RotatesWeekly_AndChangesTheCamp()
        {
            // Ember Fury every week, +10%, for as long as the cooldown.
            Assert.IsTrue(Enumerable.Range(0, 8).All(w => FraysLayout.BuffForWeek(w) == FraysLayout.ShrineBuff.Fury));
            Assert.AreEqual(180, FraysSettings.Number(FraysSettings.ShrineBuffSeconds), 1e-9);
            Assert.AreEqual(180, FraysSettings.Number(FraysSettings.ShrineCooldownSeconds), 1e-9);

            // shrine_rotate on: the old weekly turn through all four.
            values[FraysSettings.Prefix + FraysSettings.ShrineRotate] = "on";
            FraysSettings.ResetCacheForTest();
            Assert.AreEqual(4, Enumerable.Range(0, 4).Select(FraysLayout.BuffForWeek).Distinct().Count());
            Assert.AreEqual(FraysLayout.BuffForWeek(1), FraysLayout.BuffForWeek(5));
            values.Remove(FraysSettings.Prefix + FraysSettings.ShrineRotate);
            FraysSettings.ResetCacheForTest();

            var m = new FraysCampMods();
            FraysLayout.Apply(FraysLayout.ShrineBuff.Fury, m);
            Assert.AreEqual(0.10, m.BonusDamage, 1e-9);   // the Frays damage pool (was 0.05)

            m = new FraysCampMods();
            FraysLayout.Apply(FraysLayout.ShrineBuff.Ward, m);
            Assert.AreEqual(0.05, m.BonusReduction, 1e-9);

            m = new FraysCampMods();
            FraysLayout.Apply(FraysLayout.ShrineBuff.Stoke, m);
            Assert.AreEqual(1.25, m.SnarlBarSpeed, 1e-9);   // Ember Stoke

            m = new FraysCampMods();
            FraysLayout.Apply(FraysLayout.ShrineBuff.Mending, m);
            Assert.AreEqual(0.01, m.HealPer10s, 1e-9);

            foreach (var b in Enum.GetValues<FraysLayout.ShrineBuff>())
            {
                var text = FraysLayout.BuffName(b) + " " + FraysLayout.BuffText(b);
                Assert.IsTrue(text.All(ch => ch < 128), "ASCII only in player text: " + text);
            }
        }
    }
}
