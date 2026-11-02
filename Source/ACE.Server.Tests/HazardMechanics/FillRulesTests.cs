using System;
using System.Linq;
using System.Numerics;

using ACE.Server.HazardMechanics;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.HazardMechanics
{
    /// <summary>FillRules and FillSettings: tiling, damage, wisp height, hysteresis, the leg planner and Validate (Grael pit).</summary>
    [TestClass]
    public class FillRulesTests
    {
        private static HazardRegion Pit() => HazardRegionPresets.Pinnacle(0);

        private static FillSettings Defaults() => FillSettings.From(HazardArgs.Empty, Pit());

        [TestMethod]
        public void SheetTiles_Grael_Is12Tiles_EdgeToEdge()
        {
            var (edge, tiles) = FillRules.Sheet(Pit(), 10.4f, 0.3f);

            Assert.AreEqual(12, tiles.Count);

            // margined rect x -4.9..34.9 (39.8 / 4 = 9.95), y -34.9..-5.1 (29.8 / 3 = 9.933): edge 9.95, y overhangs 0.025 a side
            Assert.AreEqual(9.95f, edge, 1e-4f);

            var xs = new[] { 0.075f, 10.025f, 19.975f, 29.925f };
            var ys = new[] { -29.95f, -20.0f, -10.05f };

            for (var j = 0; j < 3; j++)
            {
                for (var i = 0; i < 4; i++)
                {
                    var t = tiles[j * 4 + i];

                    Assert.AreEqual(xs[i], t.Centre.X, 1e-4f, $"x of ({i},{j})");
                    Assert.AreEqual(ys[j], t.Centre.Y, 1e-4f, $"y of ({i},{j})");
                    Assert.AreEqual(0f, t.ZOffset, $"offset of ({i},{j}): one plane, no checkerboard step");
                }
            }

            Assert.IsTrue(tiles.All(t => t.ZOffset == 0f), "edge to edge nothing overlaps, so nothing can z-fight: no 1 cm step on any seam");
        }

        /// <summary>
        /// The 2026-10-27 dark-band bug: every pair of tiles of the Matron and Grael sheets is checked as squares of the
        /// plan's edge. No two overlap (interior intersection), every row and column neighbour shares an edge exactly
        /// (no gap), the grid covers the margined rect, and the edge is never more than panel_size.
        /// </summary>
        [DataTestMethod]
        [DataRow("matron", 12f, 0.3f, 30, 10.12f)]
        [DataRow("grael", 10.4f, 0.3f, 12, 9.95f)]
        public void Sheet_TilesAbutEdgeToEdge_NoOverlapNoGap_AndCoverTheMarginedRect(string preset, float size, float margin, int count, float expectedEdge)
        {
            var region = preset == "matron" ? HazardRegionPresets.Matron(0) : Pit();
            var (edge, tiles) = FillRules.Sheet(region, size, margin);

            Assert.AreEqual(count, tiles.Count, "the count formula is unchanged: ceil(extent / panel_size) per axis");
            Assert.AreEqual(expectedEdge, edge, 1e-3f);
            Assert.IsTrue(edge <= size + 1e-5f, "never larger than panel_size");
            Assert.AreEqual(edge, FillRules.SheetEdge(region, size, margin), 0f, "SheetEdge is the same plan");

            const float eps = 1e-3f;
            var half = edge / 2f;

            // control: the old spread (panel_size squares, first and last flush to the edges) overlaps, so the check bites
            var minXg = region.MinX - margin;
            var widthg = region.MaxX - region.MinX + 2f * margin;
            var nxg = (int)Math.Ceiling(widthg / size - 1e-6);
            var oldPitch = (widthg - size) / (nxg - 1);
            Assert.IsTrue(size - oldPitch > 0.1f, $"control: the old {size} m tiles at pitch {oldPitch:0.##} overlapped {size - oldPitch:0.##} m");

            for (var a = 0; a < tiles.Count; a++)
            {
                for (var b = a + 1; b < tiles.Count; b++)
                {
                    var dx = Math.Abs(tiles[a].Centre.X - tiles[b].Centre.X);
                    var dy = Math.Abs(tiles[a].Centre.Y - tiles[b].Centre.Y);
                    var ox = edge - dx;
                    var oy = edge - dy;

                    Assert.IsFalse(ox > eps && oy > eps, $"tiles {a} and {b} overlap {ox:0.###} x {oy:0.###} m");
                }
            }

            var xs = tiles.Select(t => t.Centre.X).Distinct(new Near()).OrderBy(v => v).ToList();
            var ys = tiles.Select(t => t.Centre.Y).Distinct(new Near()).OrderBy(v => v).ToList();

            Assert.AreEqual(tiles.Count, xs.Count * ys.Count, "a full grid");

            for (var i = 1; i < xs.Count; i++)
                Assert.AreEqual(edge, xs[i] - xs[i - 1], eps, $"column {i} abuts column {i - 1}: no gap, no overlap");

            for (var j = 1; j < ys.Count; j++)
                Assert.AreEqual(edge, ys[j] - ys[j - 1], eps, $"row {j} abuts row {j - 1}: no gap, no overlap");

            Assert.IsTrue(xs[0] - half <= region.MinX - margin + eps && xs[^1] + half >= region.MaxX + margin - eps, "covers the margined rect in x");
            Assert.IsTrue(ys[0] - half <= region.MinY - margin + eps && ys[^1] + half >= region.MaxY + margin - eps, "covers the margined rect in y");
            Assert.IsTrue(region.MinX - margin - (xs[0] - half) < edge / 2f && region.MinY - margin - (ys[0] - half) < edge / 2f, "and overhangs by less than half a tile");
        }

        [TestMethod]
        public void Validate_MatronAtPanelSize12_Passes_With30TilesAnd60Panels()
        {
            var region = HazardRegionPresets.Matron(0);
            var args = HazardArgs.Parse("element=acid panel_size=12 doubled=1 opacity=0.5 floor_z=-59.95 max_z=-44.5");

            Assert.IsNull(FillRules.Validate(region, args));
            Assert.AreEqual(30, FillRules.SheetTiles(region, 12f, 0.3f).Count, "5 x 6 tiles, 60 doubled panels, under the 64 cap");
        }

        private sealed class Near : System.Collections.Generic.IEqualityComparer<float>
        {
            public bool Equals(float a, float b) => Math.Abs(a - b) < 1e-3f;

            public int GetHashCode(float v) => 0;
        }

        [TestMethod]
        public void FlatDamage_IsBaseRampCapScale()
        {
            var s = Defaults();

            Assert.AreEqual(41.25, FillRules.FlatDamage(0.05, s, 1.0), 1e-9);
            Assert.AreEqual(90.0, FillRules.FlatDamage(2, s, 1.0), 1e-9);
            Assert.AreEqual(400.0, FillRules.FlatDamage(20, s, 1.0), 1e-9);
            Assert.AreEqual(800.0, FillRules.FlatDamage(20, s, 2.0), 1e-9);
            Assert.AreEqual(0.0, FillRules.FlatDamage(0, s, 1.0));
            Assert.AreEqual(0.0, FillRules.FlatDamage(-1, s, 1.0));
            Assert.AreEqual(0.0, FillRules.FlatDamage(2, s, 0.0));
            Assert.AreEqual(0.0, FillRules.FlatDamage(double.NaN, s, 1.0));
        }

        private static FillSettings WithSubmerged(string extra)
            => FillSettings.From(HazardArgs.Parse(extra), Pit());

        [TestMethod]
        public void FlatDamage_SubmergedTierOffByDefault_IsTheOldFormula()
        {
            var s = Defaults();

            Assert.AreEqual(0.0, s.SubmergedDamage);
            Assert.AreEqual(0.0, s.SubmergedPerM);
            Assert.AreEqual(0.0, s.SubmergedMax);

            foreach (var depth in new[] { 0.5, 2.0, 5.0 })
                Assert.AreEqual(Math.Min(40 + 25 * depth, 400.0), FillRules.FlatDamage(depth, s, 1.0), 1e-9, $"depth {depth}");

            // control: setting head_height alone does not switch the tier on
            var head = FillSettings.From(HazardArgs.Parse("head_height=1"), Pit());
            Assert.AreEqual(Math.Min(40 + 25 * 5.0, 400.0), FillRules.FlatDamage(5.0, head, 1.0), 1e-9);
        }

        [TestMethod]
        public void FlatDamage_SubmergedTier_StartsAtHeadHeightAndRampsToTheCap()
        {
            var s = WithSubmerged("damage=10 damage_per_m=10 damage_max=250 head_height=2 submerged_damage=139 submerged_per_m=25 submerged_max=500");

            Assert.AreEqual(10 + 10 * 1.99, FillRules.FlatDamage(1.99, s, 1.0), 1e-9, "just under the head: the mild curve");
            Assert.AreEqual(139.0, FillRules.FlatDamage(2.0, s, 1.0), 1e-9);
            Assert.AreEqual(189.0, FillRules.FlatDamage(4.0, s, 1.0), 1e-9);
            Assert.AreEqual(500.0, FillRules.FlatDamage(100.0, s, 1.0), 1e-9, "capped at submerged_max");
            Assert.AreEqual(278.0, FillRules.FlatDamage(2.0, s, 2.0), 1e-9, "damageScale multiplies");
            Assert.AreEqual(0.0, FillRules.FlatDamage(0.0, s, 1.0));
            Assert.AreEqual(0.0, FillRules.FlatDamage(-3.0, s, 1.0));
            Assert.AreEqual(0.0, FillRules.FlatDamage(double.NaN, s, 1.0));
            Assert.AreEqual(0.0, FillRules.FlatDamage(double.PositiveInfinity, s, 1.0));
        }

        [TestMethod]
        public void FlatDamage_SubmergedTier_TakesTheLargerOfTheTwoCurves_AndMaxZeroMeansDamageMax()
        {
            // the normal curve is larger than the tier at depth 2 (10 + 5000*2 capped 250 > 139)
            var big = WithSubmerged("damage=10 damage_per_m=5000 damage_max=250 submerged_damage=139 submerged_per_m=25 submerged_max=500");
            Assert.AreEqual(250.0, FillRules.FlatDamage(2.0, big, 1.0), 1e-9);

            // submerged_max 0 falls back to damage_max (250): 139 + 25 * 10 = 389 -> 250 from the tier, normal curve also 250
            var sameCap = WithSubmerged("damage=10 damage_per_m=10 damage_max=250 submerged_damage=139 submerged_per_m=25");
            Assert.AreEqual(0.0, sameCap.SubmergedMax);
            Assert.AreEqual(189.0, FillRules.FlatDamage(4.0, sameCap, 1.0), 1e-9);
            Assert.AreEqual(250.0, FillRules.FlatDamage(10.0, sameCap, 1.0), 1e-9);

            // a tier cap below damage_max wins only against a normal curve that is smaller
            var low = WithSubmerged("damage=1 damage_per_m=1 damage_max=250 submerged_damage=139 submerged_per_m=25 submerged_max=150");
            Assert.AreEqual(150.0, FillRules.FlatDamage(10.0, low, 1.0), 1e-9);
        }

        [TestMethod]
        public void FillSettings_SubmergedArgs_AreClampedLikeTheOtherDamageKeys()
        {
            var hi = WithSubmerged("damage=10 damage_per_m=10 damage_max=250 submerged_damage=99999 submerged_per_m=99999 submerged_max=999999");
            Assert.AreEqual(10000.0, hi.SubmergedDamage);
            Assert.AreEqual(10000.0, hi.SubmergedPerM);
            Assert.AreEqual(100000.0, hi.SubmergedMax);

            var lo = WithSubmerged("damage=10 damage_per_m=10 damage_max=250 submerged_damage=-5 submerged_per_m=-5 submerged_max=-5");
            Assert.AreEqual(0.0, lo.SubmergedDamage);
            Assert.AreEqual(0.0, lo.SubmergedPerM);
            Assert.AreEqual(0.0, lo.SubmergedMax);

            foreach (var key in new[] { "submerged_damage", "submerged_per_m", "submerged_max" })
                CollectionAssert.Contains(FillSettings.NumberKeys, key);
        }

        [TestMethod]
        public void FillRules_WispZ_ClampsBetweenFloorAndCeiling()
        {
            Assert.AreEqual(-47.70f, FillRules.WispZ(-47.70, -47.75, -42.05, 0.5f), 1e-4f, "never into the floor plates");
            Assert.AreEqual(-46.4575f, FillRules.WispZ(-46.0, -47.75, -42.05, 0.5f), 1e-4f, "half its height under the surface");
            Assert.AreEqual(-43.065f, FillRules.WispZ(-42.35, -47.75, -42.05, 0.5f), 1e-4f, "stops under the ceiling");
            Assert.AreEqual(-42.8075f, FillRules.WispZ(-42.35, -47.75, null, 0.5f), 1e-4f, "no ceiling, no upper clamp");
        }

        [TestMethod]
        public void UnderSheetShown_Hysteresis()
        {
            Assert.IsFalse(FillRules.UnderSheetShown(false, -45.70, -45.75, 0.1));
            Assert.IsTrue(FillRules.UnderSheetShown(false, -45.65, -45.75, 0.1));
            Assert.IsTrue(FillRules.UnderSheetShown(true, -45.70, -45.75, 0.1));
            Assert.IsFalse(FillRules.UnderSheetShown(true, -45.76, -45.75, 0.1));
        }

        [TestMethod]
        public void NextLeg_Defaults_TenSecondsToTheResnap()
        {
            var s = Defaults();
            var surface = new FillSurface(s.FloorZ, s.MaxZ, s.StartZ, s.Rate);

            var leg = FillRules.NextLeg(surface, 1.0, s.Resnap).Value;

            Assert.AreEqual(-47.15, leg.EndZ, 1e-6);
            Assert.AreEqual(10.0, leg.Seconds, 1e-9);

            surface.SetRate(1.0, 0.0);
            Assert.IsNull(FillRules.NextLeg(surface, 1.0, s.Resnap));
        }

        [TestMethod]
        public void Settings_Defaults_OnTheGraelPit()
        {
            var s = Defaults();

            Assert.AreEqual("acid", s.Element);
            Assert.AreEqual("acid", s.Colour);
            Assert.AreEqual(ACE.Entity.Enum.DamageType.Acid, s.DamageType);
            Assert.AreEqual(-47.75, s.GroundZ, 1e-4);
            Assert.AreEqual(-47.70, s.FloorZ, 1e-4);
            Assert.AreEqual(-42.35, s.MaxZ, 1e-4);
            Assert.AreEqual(-47.70, s.StartZ, 1e-4);
            Assert.AreEqual(16, s.Wisps);
            Assert.AreEqual(HazardDefaults.FillAcidWispWcid, s.WispWcid);

            var lava = FillSettings.From(HazardArgs.Parse("element=lava"), Pit());

            Assert.AreEqual("lava", lava.Colour);
            Assert.AreEqual(ACE.Entity.Enum.DamageType.Fire, lava.DamageType);
            Assert.AreEqual(HazardDefaults.FillLavaWispWcid, lava.WispWcid);
        }

        [TestMethod]
        public void Settings_Defaults_AreDoubledAtHalfOpacity()
        {
            var s = Defaults();

            Assert.IsTrue(s.Doubled);
            Assert.AreEqual(0.5, s.Opacity, 1e-9);

            var off = FillSettings.From(HazardArgs.Parse("doubled=0 opacity=1"), Pit());

            Assert.IsFalse(off.Doubled);
            Assert.AreEqual(1.0, off.Opacity, 1e-9);
            Assert.AreEqual(1.0, FillSettings.From(HazardArgs.Parse("opacity=7"), Pit()).Opacity, 1e-9, "still clamped to 0..1");
            Assert.AreEqual(0.0, FillSettings.From(HazardArgs.Parse("opacity=-1"), Pit()).Opacity, 1e-9);
        }

        [TestMethod]
        public void Validate_TheCapCountsSurfacePanels_DoubledTwoPerTile()
        {
            // 50 x 60 m room at panel_size 10 with the default 0.3 m sheet margin: 50.6 / 10 -> 6 columns, 60.6 / 10 -> 7 rows
            var room = HazardRegion.AdHoc(0x0069, 0, false, new Vector3(0, 0, -47.75f), HazardShapeSpec.Rect(50f, 60f), 0f, out _);
            var tiles = FillRules.SheetTiles(room, 10f, 0.3f).Count;

            Assert.AreEqual(42, tiles, "6 x 7 tiles");
            Assert.IsTrue(tiles <= FillRules.MaxSheetTiles && tiles * 2 > FillRules.MaxSheetTiles, "passes single, exceeds 64 doubled");

            Assert.IsNull(FillRules.Validate(room, HazardArgs.Parse("max_z=-45 panel_size=10 doubled=0")), "42 single panels pass");

            var refused = FillRules.Validate(room, HazardArgs.Parse("max_z=-45 panel_size=10"));

            Assert.AreEqual("fill sheet needs 84 surface panels (42 tiles x 2 for doubled=1); at most 64 (use doubled=0 or a larger panel_size)", refused);

            Assert.IsNull(FillRules.Validate(room, HazardArgs.Parse("max_z=-45 panel_size=12")), "a larger panel_size brings it back under");
            Assert.IsNull(FillRules.Validate(Pit(), HazardArgs.Empty), "Grael: 12 tiles, 24 panels doubled");
        }

        [TestMethod]
        public void Validate_PinnacleNoArgs_IsNull_AndReachingTheTopIsAccepted()
        {
            Assert.IsNull(FillRules.Validate(Pit(), HazardArgs.Empty));
            Assert.IsNull(FillRules.Validate(Pit(), HazardArgs.Parse("rate=2 duration=0")), "host-escaped: a run that reaches max_z is not refused");
        }

        [TestMethod]
        public void Validate_RefusesWithOneLine_NeverThrows()
        {
            var circle = HazardRegion.AdHoc(0x0069, 0, false, new Vector3(0, 0, 0), HazardShapeSpec.Circle(10f), 0f, out _);
            var terrain = HazardRegion.AdHoc(0xA9B4, 0, true, new Vector3(96f, 96f, 30f), HazardShapeSpec.Rect(20f, 20f), 0f, out _);
            var noCeiling = HazardRegion.AdHoc(0x0069, 0, false, new Vector3(0, 0, -47.75f), HazardShapeSpec.Rect(20f, 20f), 0f, out _);

            foreach (var (region, args) in new[]
            {
                (circle, ""),
                (terrain, ""),
                (noCeiling, ""),
                (Pit(), "element=mud"),
                (Pit(), "color=mud"),
                (Pit(), "rate=fast"),
                (Pit(), "damage_type=holy"),
                (Pit(), "floor_z=-42.4"),
                (Pit(), "max_z=-42.0"),
                (Pit(), "start_z=-10"),
                (Pit(), "panel_size=1"),
                (Pit(), "wisp_wcid=1.5"),
            })
            {
                var line = FillRules.Validate(region, HazardArgs.Parse(args));

                Assert.IsNotNull(line, args);
                Assert.IsFalse(line.Contains('\n'), "one line: " + line);
            }

            Assert.AreEqual("max_z is required when the region has no ceiling", FillRules.Validate(noCeiling, HazardArgs.Empty));
            Assert.IsNull(FillRules.Validate(noCeiling, HazardArgs.Parse("max_z=-45")));
            Assert.IsNotNull(FillRules.Validate(null, HazardArgs.Empty));
        }
    }
}
