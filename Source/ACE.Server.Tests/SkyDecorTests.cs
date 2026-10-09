using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Models.World;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// WaffleACE fork: sky decor. Everything here runs without a database, a dat file or a world - the
    /// generation math lives in ACE.Server.Entity.SkyDecorLayout precisely so that it can be tested this
    /// way, and the persistence proof only needs a WorldObject built from a hand-made Weenie.
    /// </summary>
    [TestClass]
    public class SkyDecorTests
    {
        private const ushort MaraeBluespire = 0x21B0;

        /// <summary>Flat ground at 12 m, so a wrong Z is visible as a wrong number rather than as zero.</summary>
        private static float FlatGround(float x, float y) => 12.0f;

        private static SkyDecorRegion MakeRegion(Action<SkyDecorRegion> tweak = null)
        {
            var region = new SkyDecorRegion
            {
                Id = 1,
                Name = "test",
                Enabled = true,
                RealmId = 1,
                LbXMin = 0x1F,
                LbXMax = 0x23,
                LbYMin = 0xAE,
                LbYMax = 0xB2,
                Seed = 20260820,
                Version = 1,
                Density = 5.0f,
                // White 1002648 is retired from lineups (its sparse quad renders pixelated at sky scale),
                // so it is deliberately absent here too - the fixture mirrors the shipped row.
                Palette = "1002642,1002643,1002644,1002645,1002646,1002647,1002649",
                ScaleMin = 12.0f,
                ScaleMax = 60.0f,
                HeightMin = 25.0f,
                HeightMax = 50.0f,
                TiltMaxDeg = 5.0f,
                SpinMin = 0.7f,
                SpinMax = 1.3f,
                PartOffset = 0.455f,
                OverWater = true,
                PairChance = 1.0f,
                PairScaleRatio = 0.75f,
                PairGap = 0.6f,
                PairSpeedRatio = 1.3333f,
            };

            tweak?.Invoke(region);

            return region;
        }

        /// <summary>
        /// A stable text form of a whole plan, PARTNERS INCLUDED, so the determinism tests cover the
        /// pairing draws as well as the primary ones.
        /// </summary>
        private static string Fingerprint(List<SkyDecorCloud> clouds)
        {
            return string.Join("|", clouds.SelectMany(c => c.WithPartner()).Select(c =>
                $"{c.WeenieClassId}:{c.CellX},{c.CellY}:{c.X:F5},{c.Y:F5},{c.OriginZ:F5}:{c.Scale:F5}:{c.Spin:F5}:{c.TiltDeg:F5}:{c.YawDeg:F5}:{c.AnglesW:F5},{c.AnglesX:F5},{c.AnglesY:F5},{c.AnglesZ:F5}"));
        }

        /// <summary>Just the primaries, for asserting that a pairing change left them alone.</summary>
        private static string PrimaryFingerprint(List<SkyDecorCloud> clouds)
        {
            return string.Join("|", clouds.Select(c =>
                $"{c.WeenieClassId}:{c.CellX},{c.CellY}:{c.X:F5},{c.Y:F5},{c.OriginZ:F5}:{c.Scale:F5}:{c.Spin:F5}:{c.TiltDeg:F5}:{c.YawDeg:F5}"));
        }

        // =======================================================================================
        // StableHash
        // =======================================================================================

        /// <summary>
        /// The golden value. If this ever changes, EVERY existing region's layout has changed with it -
        /// which is a content decision, not a refactor. HashCode.Combine and string.GetHashCode are both
        /// randomized per process and could not pass a test like this at all; that is why neither is used.
        /// </summary>
        [TestMethod]
        public void StableHash_KnownInput_ReturnsFixedValue()
        {
            Assert.AreEqual(0x9B6A1A19u, SkyDecorLayout.StableHash(20260820, 1, 1, MaraeBluespire));
        }

        [TestMethod]
        public void StableHash_IsStableWithinAndAcrossCalls()
        {
            var first = SkyDecorLayout.StableHash(20260820, 1, 1, MaraeBluespire);

            for (var i = 0; i < 100; i++)
                Assert.AreEqual(first, SkyDecorLayout.StableHash(20260820, 1, 1, MaraeBluespire));
        }

        [TestMethod]
        public void StableHash_EveryTermChangesTheResult()
        {
            var baseline = SkyDecorLayout.StableHash(20260820, 1, 1, MaraeBluespire);

            Assert.AreNotEqual(baseline, SkyDecorLayout.StableHash(20260821, 1, 1, MaraeBluespire), "seed must matter");
            Assert.AreNotEqual(baseline, SkyDecorLayout.StableHash(20260820, 2, 1, MaraeBluespire), "version must matter");
            Assert.AreNotEqual(baseline, SkyDecorLayout.StableHash(20260820, 1, 0, MaraeBluespire), "realm must matter");
            Assert.AreNotEqual(baseline, SkyDecorLayout.StableHash(20260820, 1, 1, 0x21B1), "landblock must matter");
        }

        // =======================================================================================
        // Determinism
        // =======================================================================================

        [TestMethod]
        public void Plan_SameInputsTwice_ProducesIdenticalLayout()
        {
            var a = SkyDecorLayout.Plan(MakeRegion(), MaraeBluespire, FlatGround, null);
            var b = SkyDecorLayout.Plan(MakeRegion(), MaraeBluespire, FlatGround, null);

            Assert.IsTrue(a.Count > 0, "the test region must actually produce clouds");
            Assert.AreEqual(Fingerprint(a), Fingerprint(b));
        }

        [TestMethod]
        public void Plan_DifferentLandblock_ProducesDifferentLayout()
        {
            var a = SkyDecorLayout.Plan(MakeRegion(), MaraeBluespire, FlatGround, null);
            var b = SkyDecorLayout.Plan(MakeRegion(), 0x21B1, FlatGround, null);

            Assert.AreNotEqual(Fingerprint(a), Fingerprint(b));
        }

        [TestMethod]
        public void Plan_DifferentVersion_ProducesDifferentLayout()
        {
            var a = SkyDecorLayout.Plan(MakeRegion(), MaraeBluespire, FlatGround, null);
            var b = SkyDecorLayout.Plan(MakeRegion(r => r.Version = 2), MaraeBluespire, FlatGround, null);

            Assert.AreNotEqual(Fingerprint(a), Fingerprint(b));
        }

        [TestMethod]
        public void Plan_DifferentSeed_ProducesDifferentLayout()
        {
            var a = SkyDecorLayout.Plan(MakeRegion(), MaraeBluespire, FlatGround, null);
            var b = SkyDecorLayout.Plan(MakeRegion(r => r.Seed = 20260821), MaraeBluespire, FlatGround, null);

            Assert.AreNotEqual(Fingerprint(a), Fingerprint(b));
        }

        /// <summary>
        /// The layout of one landblock must not depend on any other landblock, on load order, or on how
        /// many blocks have been planned before it - planning the whole 5x5 region in reverse must give
        /// each block the same clouds it got when planned alone.
        /// </summary>
        [TestMethod]
        public void Plan_IsIndependentOfPlanningOrder()
        {
            var alone = new Dictionary<ushort, string>();

            for (var x = 0x1F; x <= 0x23; x++)
            {
                for (var y = 0xAE; y <= 0xB2; y++)
                {
                    var lb = (ushort)((x << 8) | y);
                    alone[lb] = Fingerprint(SkyDecorLayout.Plan(MakeRegion(), lb, FlatGround, null));
                }
            }

            foreach (var lb in alone.Keys.OrderByDescending(k => k))
                Assert.AreEqual(alone[lb], Fingerprint(SkyDecorLayout.Plan(MakeRegion(), lb, FlatGround, null)), $"landblock 0x{lb:X4}");
        }

        // =======================================================================================
        // Translucency
        // =======================================================================================

        /// <summary>
        /// Turning the translucency dial on has to be invisible to every OTHER field a cloud carries:
        /// changing the RANGE must not move, resize or recolour anything. TranslucencyRoll is an
        /// independent hash rather than an `rng.` draw precisely so this holds.
        ///
        /// Know what this does NOT catch, because it looks like it should: both plans below are built by
        /// the SAME code, so an edit that turned the roll into a stream draw would shift both streams
        /// identically and this test would still pass. The guard against THAT is the golden in
        /// MaraeIsland_AsShipped_HasAKnownSurvivorTotal - its 3019/1062/2124 are hard-coded from before
        /// this dial existed, and a shifted stream moves every disc, which moves the overlap cull, which
        /// moves those totals. The two tests are complementary; neither is sufficient alone.
        /// </summary>
        [TestMethod]
        public void Layout_AddingTranslucency_DoesNotMoveASingleCloud()
        {
            for (var x = 0x1F; x <= 0x23; x++)
            {
                for (var y = 0xAE; y <= 0xB2; y++)
                {
                    var lb = (ushort)((x << 8) | y);

                    var opaque = SkyDecorLayout.Plan(MakeRegion(), lb, FlatGround, null);
                    var translucent = SkyDecorLayout.Plan(MakeRegion(r => { r.TranslucencyMin = 0.2f; r.TranslucencyMax = 0.8f; }), lb, FlatGround, null);

                    Assert.AreEqual(opaque.Count, translucent.Count, $"landblock 0x{lb:X4}: cloud count moved");

                    var a = opaque.SelectMany(c => c.WithPartner()).ToList();
                    var b = translucent.SelectMany(c => c.WithPartner()).ToList();

                    Assert.AreEqual(a.Count, b.Count, $"landblock 0x{lb:X4}: partner count moved");

                    for (var i = 0; i < a.Count; i++)
                    {
                        Assert.AreEqual(a[i].WeenieClassId, b[i].WeenieClassId, $"landblock 0x{lb:X4} cloud {i}: WeenieClassId moved");
                        Assert.AreEqual(a[i].X, b[i].X, 1e-6f, $"landblock 0x{lb:X4} cloud {i}: X moved");
                        Assert.AreEqual(a[i].Y, b[i].Y, 1e-6f, $"landblock 0x{lb:X4} cloud {i}: Y moved");
                        Assert.AreEqual(a[i].Scale, b[i].Scale, 1e-6f, $"landblock 0x{lb:X4} cloud {i}: Scale moved");
                        Assert.AreEqual(a[i].Height, b[i].Height, 1e-5f, $"landblock 0x{lb:X4} cloud {i}: Height moved");
                        Assert.AreEqual(a[i].TiltDeg, b[i].TiltDeg, 1e-6f, $"landblock 0x{lb:X4} cloud {i}: TiltDeg moved");
                        Assert.AreEqual(a[i].YawDeg, b[i].YawDeg, 1e-6f, $"landblock 0x{lb:X4} cloud {i}: YawDeg moved");
                        Assert.AreEqual(a[i].Spin, b[i].Spin, 1e-6f, $"landblock 0x{lb:X4} cloud {i}: Spin moved");

                        // The opaque plan's Translucency is 0 (MakeRegion's default), so this is also the
                        // proof that the field is the ONLY thing the dial changed.
                        Assert.AreEqual(0.0f, a[i].Translucency, $"landblock 0x{lb:X4} cloud {i}: opaque baseline was not 0");
                    }
                }
            }
        }

        /// <summary>
        /// Values land in the requested band, two Plan calls agree exactly (same determinism guarantee as
        /// every other field), a partner inherits its primary's value verbatim, and a 0/0 region still
        /// yields exactly 0 - the roll is a real dial, not a fixed offset.
        /// </summary>
        [TestMethod]
        public void Layout_Translucency_IsInRangeDeterministicAndSharedByAPair()
        {
            var a = SkyDecorLayout.Plan(MakeRegion(r => { r.TranslucencyMin = 0.2f; r.TranslucencyMax = 0.8f; }), MaraeBluespire, FlatGround, null);
            var b = SkyDecorLayout.Plan(MakeRegion(r => { r.TranslucencyMin = 0.2f; r.TranslucencyMax = 0.8f; }), MaraeBluespire, FlatGround, null);

            Assert.IsTrue(a.Count > 0, "the test region must actually produce clouds");
            Assert.AreEqual(a.Count, b.Count);

            for (var i = 0; i < a.Count; i++)
            {
                Assert.IsTrue(a[i].Translucency >= 0.2f && a[i].Translucency <= 0.8f, $"cloud {i}: {a[i].Translucency} outside [0.2, 0.8]");
                Assert.AreEqual(a[i].Translucency, b[i].Translucency, 1e-9f, $"cloud {i}: two Plan calls disagreed");

                if (a[i].Partner != null)
                    Assert.AreEqual(a[i].Translucency, a[i].Partner.Translucency, 1e-9f, $"cloud {i}: partner did not inherit the primary's value");
            }

            var zero = SkyDecorLayout.Plan(MakeRegion(), MaraeBluespire, FlatGround, null);

            Assert.IsTrue(zero.Count > 0);

            foreach (var c in zero.SelectMany(c => c.WithPartner()))
                Assert.AreEqual(0.0f, c.Translucency, $"a 0/0 region must yield exactly 0, got {c.Translucency}");
        }

        // =======================================================================================
        // Density
        // =======================================================================================

        [TestMethod]
        public void Plan_FractionalDensity_AveragesToDensityAndStaysBetweenTheWholeParts()
        {
            var total = 0;
            var blocks = 0;

            // 2.5 clouds per block over the whole 256x256 landblock grid: enough samples that a broken
            // fractional part shows up as an average nowhere near 2.5.
            for (var x = 0; x <= 0xFF; x++)
            {
                for (var y = 0; y <= 0xFF; y++)
                {
                    var lb = (ushort)((x << 8) | y);

                    var clouds = SkyDecorLayout.Plan(MakeRegion(r => r.Density = 2.5f), lb, FlatGround, null);

                    Assert.IsTrue(clouds.Count >= 2 && clouds.Count <= 3, $"landblock 0x{lb:X4} produced {clouds.Count} clouds at density 2.5");

                    total += clouds.Count;
                    blocks++;
                }
            }

            var average = (double)total / blocks;

            Assert.IsTrue(Math.Abs(average - 2.5) < 0.02, $"average was {average:F4}, expected ~2.5");
        }

        [TestMethod]
        public void Plan_WholeDensity_AlwaysProducesExactlyThatMany()
        {
            for (var y = 0xAE; y <= 0xB2; y++)
            {
                var lb = (ushort)((0x21 << 8) | y);

                Assert.AreEqual(4, SkyDecorLayout.Plan(MakeRegion(r => r.Density = 4.0f), lb, FlatGround, null).Count);
            }
        }

        /// <summary>
        /// The shipped Marae values: 2.5 clouds per block must give 2 or 3, and BOTH must actually occur
        /// across the 25 blocks the row covers - an average that only ever rounds one way would mean the
        /// fractional draw was not really per block.
        /// </summary>
        [TestMethod]
        public void Plan_MaraeDensity_GivesTwoOrThreeAndBothOccurAcrossTheRegion()
        {
            var counts = new List<int>();

            for (var x = 0x1F; x <= 0x23; x++)
            {
                for (var y = 0xAE; y <= 0xB2; y++)
                {
                    var lb = (ushort)((x << 8) | y);

                    var n = SkyDecorLayout.Plan(MakeRegion(r => r.Density = 2.5f), lb, FlatGround, null).Count;

                    Assert.IsTrue(n == 2 || n == 3, $"landblock 0x{lb:X4} produced {n} clouds at density 2.5");

                    counts.Add(n);
                }
            }

            Assert.AreEqual(25, counts.Count);
            Assert.IsTrue(counts.Contains(2), "no block got the guaranteed-only count of 2");
            Assert.IsTrue(counts.Contains(3), "no block got the extra cloud - the fractional draw is not firing");
        }

        [TestMethod]
        public void Plan_DensityBelowOne_GivesZeroOrOne()
        {
            var counts = new HashSet<int>();

            for (var x = 0; x <= 0x3F; x++)
            {
                for (var y = 0; y <= 0x3F; y++)
                {
                    var n = SkyDecorLayout.Plan(MakeRegion(r => r.Density = 0.5f), (ushort)((x << 8) | y), FlatGround, null).Count;

                    Assert.IsTrue(n == 0 || n == 1, $"density 0.5 produced {n} clouds");

                    counts.Add(n);
                }
            }

            CollectionAssert.AreEquivalent(new[] { 0, 1 }, counts.ToArray());
        }

        /// <summary>
        /// A whole density consumes the fractional draw anyway, so raising 2 -> 2.5 ADDS a third cloud
        /// without moving the two that were already there. If the draw were conditional, every later
        /// draw would shift and the whole block would reshuffle.
        /// </summary>
        [TestMethod]
        public void Plan_RaisingDensityByAFraction_KeepsTheExistingClouds()
        {
            var kept = 0;

            for (var y = 0xAE; y <= 0xB2; y++)
            {
                var lb = (ushort)((0x21 << 8) | y);

                var two = SkyDecorLayout.Plan(MakeRegion(r => r.Density = 2.0f), lb, FlatGround, null);
                var twoAndAHalf = SkyDecorLayout.Plan(MakeRegion(r => r.Density = 2.5f), lb, FlatGround, null);

                Assert.AreEqual(2, two.Count);

                CollectionAssert.AreEqual(
                    two.Select(c => (c.CellX, c.CellY, c.WeenieClassId)).ToArray(),
                    twoAndAHalf.Take(2).Select(c => (c.CellX, c.CellY, c.WeenieClassId)).ToArray(),
                    $"landblock 0x{lb:X4}: raising density moved the existing clouds");

                kept++;
            }

            Assert.AreEqual(5, kept);
        }

        [TestMethod]
        public void Plan_ZeroOrNegativeDensity_ProducesNothing()
        {
            Assert.AreEqual(0, SkyDecorLayout.Plan(MakeRegion(r => r.Density = 0.0f), MaraeBluespire, FlatGround, null).Count);
            Assert.AreEqual(0, SkyDecorLayout.Plan(MakeRegion(r => r.Density = -3.0f), MaraeBluespire, FlatGround, null).Count);
        }

        /// <summary>One cloud per terrain cell at most, so a silly density cannot stack 200 discs on one spot.</summary>
        [TestMethod]
        public void Plan_DensityAboveCellCount_IsCappedAtOnePerCell()
        {
            var clouds = SkyDecorLayout.Plan(MakeRegion(r => r.Density = 500.0f), MaraeBluespire, FlatGround, null);

            Assert.AreEqual(64, clouds.Count);
            Assert.AreEqual(64, clouds.Select(c => (c.CellX, c.CellY)).Distinct().Count());
        }

        [TestMethod]
        public void Plan_CellsAreDistinct()
        {
            var clouds = SkyDecorLayout.Plan(MakeRegion(), MaraeBluespire, FlatGround, null);

            Assert.AreEqual(clouds.Count, clouds.Select(c => (c.CellX, c.CellY)).Distinct().Count());
        }

        // =======================================================================================
        // Bounds
        // =======================================================================================

        [TestMethod]
        public void Plan_EveryCloudIsWithinItsRegionsBounds()
        {
            var region = MakeRegion();

            var checkedClouds = 0;

            for (var x = 0x1F; x <= 0x23; x++)
            {
                for (var y = 0xAE; y <= 0xB2; y++)
                {
                    var lb = (ushort)((x << 8) | y);

                    foreach (var c in SkyDecorLayout.Plan(region, lb, FlatGround, null))
                    {
                        Assert.IsTrue(c.CellX >= 0 && c.CellX <= 7, $"cellX {c.CellX}");
                        Assert.IsTrue(c.CellY >= 0 && c.CellY <= 7, $"cellY {c.CellY}");

                        Assert.IsTrue(c.X >= 0.0f && c.X < 192.0f, $"x {c.X}");
                        Assert.IsTrue(c.Y >= 0.0f && c.Y < 192.0f, $"y {c.Y}");

                        // the jitter must never carry a cloud out of the cell it was assigned
                        Assert.AreEqual(c.CellX, (int)(c.X / 24.0f), "x is outside its own cell");
                        Assert.AreEqual(c.CellY, (int)(c.Y / 24.0f), "y is outside its own cell");

                        Assert.IsTrue(c.Scale >= region.ScaleMin && c.Scale <= region.ScaleMax, $"scale {c.Scale}");
                        Assert.IsTrue(c.Height >= region.HeightMin && c.Height <= region.HeightMax, $"height {c.Height}");
                        Assert.IsTrue(c.Spin >= region.SpinMin && c.Spin <= region.SpinMax, $"spin {c.Spin}");
                        Assert.IsTrue(c.TiltDeg >= 0.0f && c.TiltDeg <= region.TiltMaxDeg, $"tilt {c.TiltDeg}");
                        Assert.IsTrue(c.YawDeg >= 0.0f && c.YawDeg < 360.0f, $"yaw {c.YawDeg}");

                        checkedClouds++;
                    }
                }
            }

            Assert.IsTrue(checkedClouds >= 100, $"only {checkedClouds} clouds were checked");
        }

        [TestMethod]
        public void CellId_MatchesThePhysicsLandcellFormula()
        {
            // Physics.Common.Landblock.GetCell: (landblock << 16) | (cellX * 8 + cellY) + 1
            Assert.AreEqual(0x21B00001u, SkyDecorLayout.CellId(MaraeBluespire, 5.0f, 5.0f));
            Assert.AreEqual(0x21B00040u, SkyDecorLayout.CellId(MaraeBluespire, 190.0f, 190.0f));
            Assert.AreEqual(0x21B00012u, SkyDecorLayout.CellId(MaraeBluespire, 2 * 24.0f + 12.0f, 1 * 24.0f + 12.0f));
        }

        // =======================================================================================
        // Orientation and height
        // =======================================================================================

        /// <summary>
        /// tilt 0 is the approved flat pose: a 180 degree roll about a horizontal axis, so W is 0 and the
        /// remaining unit vector is the yawed axis. The disc then hangs partOffset * scale BELOW the
        /// origin, which is why the origin sits that far above the requested disc height.
        /// </summary>
        [TestMethod]
        public void Orientation_ZeroTilt_IsAFlatHangingDisc()
        {
            for (var yaw = 0.0f; yaw < 360.0f; yaw += 17.0f)
            {
                SkyDecorLayout.Orientation(yaw, 0.0f, out var w, out var x, out var y, out var z);

                Assert.AreEqual(0.0f, w, 1e-6f, $"W at yaw {yaw}");
                Assert.AreEqual(0.0f, z, 1e-6f, $"Z at yaw {yaw}");
                Assert.AreEqual(1.0f, (float)Math.Sqrt(x * x + y * y), 1e-5f, $"XY must be a unit vector at yaw {yaw}");
            }

            // the canonical row the hand-placed probe used: w = 0, x = 1
            SkyDecorLayout.Orientation(0.0f, 0.0f, out var w0, out var x0, out var y0, out var z0);

            Assert.AreEqual(0.0f, w0, 1e-6f);
            Assert.AreEqual(1.0f, x0, 1e-6f);
            Assert.AreEqual(0.0f, y0, 1e-6f);
            Assert.AreEqual(0.0f, z0, 1e-6f);
        }

        [TestMethod]
        public void Orientation_IsAlwaysAUnitQuaternion()
        {
            for (var yaw = 0.0f; yaw < 360.0f; yaw += 13.0f)
            {
                for (var tilt = 0.0f; tilt <= 45.0f; tilt += 3.0f)
                {
                    SkyDecorLayout.Orientation(yaw, tilt, out var w, out var x, out var y, out var z);

                    Assert.AreEqual(1.0f, (float)Math.Sqrt(w * w + x * x + y * y + z * z), 1e-5f, $"yaw {yaw} tilt {tilt}");
                }
            }
        }

        /// <summary>
        /// The height column means DISC CENTRE, not origin. At tilt 0 the disc is exactly ground + height,
        /// and the origin is partOffset * scale above that.
        /// </summary>
        [TestMethod]
        public void OriginZ_ZeroTilt_PutsTheDiscCentreAtGroundPlusHeight()
        {
            var originZ = SkyDecorLayout.OriginZ(12.0f, 40.0f, 0.455f, 25.0f, 0.0f);

            Assert.AreEqual(12.0f + 40.0f + 0.455f * 25.0f, originZ, 1e-4f);

            // and the disc, which hangs straight down from the origin, is back at ground + height
            Assert.AreEqual(12.0f + 40.0f, originZ - 0.455f * 25.0f, 1e-4f);
        }

        [TestMethod]
        public void OriginZ_TiltedDiscCentreStillLandsAtGroundPlusHeight()
        {
            const float ground = 12.0f;
            const float height = 40.0f;
            const float partOffset = 0.455f;
            const float scale = 30.0f;

            foreach (var tilt in new[] { 0.0f, 5.0f, 15.0f, 45.0f })
            {
                var originZ = SkyDecorLayout.OriginZ(ground, height, partOffset, scale, tilt);

                // the part sits partOffset * scale along local +Z, whose vertical component after the
                // (180 - tilt) roll is cos(180 - tilt)
                var discZ = originZ + partOffset * scale * (float)Math.Cos((180.0 - tilt) * Math.PI / 180.0);

                Assert.AreEqual(ground + height, discZ, 1e-3f, $"tilt {tilt}");
            }
        }

        [TestMethod]
        public void Plan_OriginZ_IsConsistentWithTheHeightColumn()
        {
            var region = MakeRegion();

            foreach (var c in SkyDecorLayout.Plan(region, MaraeBluespire, FlatGround, null))
            {
                var discZ = c.OriginZ + region.PartOffset * c.Scale * (float)Math.Cos((180.0 - c.TiltDeg) * Math.PI / 180.0);

                Assert.AreEqual(c.GroundZ + c.Height, discZ, 1e-2f);
            }
        }

        // =======================================================================================
        // Palette
        // =======================================================================================

        [TestMethod]
        public void ParsePalette_HandlesBothForms()
        {
            var entries = SkyDecorLayout.ParsePalette("1002642:3, 1002643 ,1002648:1");

            Assert.AreEqual(3, entries.Count);

            Assert.AreEqual(1002642u, entries[0].WeenieClassId);
            Assert.AreEqual(3, entries[0].Weight);

            Assert.AreEqual(1002643u, entries[1].WeenieClassId);
            Assert.AreEqual(1, entries[1].Weight, "a missing weight is 1");

            Assert.AreEqual(1002648u, entries[2].WeenieClassId);
            Assert.AreEqual(1, entries[2].Weight);
        }

        [TestMethod]
        public void ParsePalette_DropsGarbageAndKeepsTheRest()
        {
            var entries = SkyDecorLayout.ParsePalette("1002642, notawcid, 0, 1002643:x, ,1002648:2");

            CollectionAssert.AreEqual(new[] { 1002642u, 1002648u }, entries.Select(e => e.WeenieClassId).ToArray());
        }

        [TestMethod]
        public void Plan_SingleEntryPalette_AlwaysPicksThatWcid()
        {
            var clouds = SkyDecorLayout.Plan(MakeRegion(r => r.Palette = "1002648"), MaraeBluespire, FlatGround, null);

            Assert.IsTrue(clouds.Count > 0);
            Assert.IsTrue(clouds.All(c => c.WeenieClassId == 1002648u));
        }

        [TestMethod]
        public void Plan_ZeroWeightEntry_IsNeverPicked()
        {
            var picked = new HashSet<uint>();

            for (var x = 0; x <= 0x3F; x++)
            {
                for (var y = 0; y <= 0x3F; y++)
                {
                    var lb = (ushort)((x << 8) | y);

                    foreach (var c in SkyDecorLayout.Plan(MakeRegion(r => r.Palette = "1002642:0,1002643:1,1002644:0,1002645:2"), lb, FlatGround, null))
                        picked.Add(c.WeenieClassId);
                }
            }

            Assert.IsFalse(picked.Contains(1002642u), "a zero-weight entry was picked");
            Assert.IsFalse(picked.Contains(1002644u), "a zero-weight entry was picked");
            Assert.IsTrue(picked.Contains(1002643u));
            Assert.IsTrue(picked.Contains(1002645u));
        }

        [TestMethod]
        public void Plan_PaletteWeights_AreHonouredInProportion()
        {
            var counts = new Dictionary<uint, int> { { 1002642u, 0 }, { 1002643u, 0 } };

            for (var x = 0; x <= 0x7F; x++)
            {
                for (var y = 0; y <= 0x7F; y++)
                {
                    var lb = (ushort)((x << 8) | y);

                    foreach (var c in SkyDecorLayout.Plan(MakeRegion(r => r.Palette = "1002642:3,1002643:1"), lb, FlatGround, null))
                        counts[c.WeenieClassId]++;
                }
            }

            var ratio = (double)counts[1002642u] / counts[1002643u];

            Assert.IsTrue(Math.Abs(ratio - 3.0) < 0.15, $"weight 3:1 produced a {ratio:F3}:1 split");
        }

        [TestMethod]
        public void Plan_EmptyOrAllZeroPalette_ProducesNothing()
        {
            Assert.AreEqual(0, SkyDecorLayout.Plan(MakeRegion(r => r.Palette = ""), MaraeBluespire, FlatGround, null).Count);
            Assert.AreEqual(0, SkyDecorLayout.Plan(MakeRegion(r => r.Palette = "1002642:0,1002643:0"), MaraeBluespire, FlatGround, null).Count);
        }

        // =======================================================================================
        // Pairing
        // =======================================================================================

        [TestMethod]
        public void Plan_PairChanceOne_GivesEveryCloudAPartner()
        {
            var clouds = SkyDecorLayout.Plan(MakeRegion(), MaraeBluespire, FlatGround, null);

            Assert.IsTrue(clouds.Count > 0);
            Assert.AreEqual(clouds.Count, clouds.Count(c => c.Partner != null));

            // a partner is never itself paired - the nesting is exactly one deep
            Assert.IsTrue(clouds.All(c => c.Partner.Partner == null));

            // and the flattened form is what the landblock spawns
            Assert.AreEqual(clouds.Count * 2, clouds.SelectMany(c => c.WithPartner()).Count());
        }

        [TestMethod]
        public void Plan_PairChanceZero_GivesNoPartners()
        {
            var clouds = SkyDecorLayout.Plan(MakeRegion(r => r.PairChance = 0.0f), MaraeBluespire, FlatGround, null);

            Assert.IsTrue(clouds.Count > 0);
            Assert.AreEqual(0, clouds.Count(c => c.Partner != null));
            Assert.AreEqual(clouds.Count, clouds.SelectMany(c => c.WithPartner()).Count());
        }

        /// <summary>
        /// Turning pairing off must not move a single primary disc. That is why the two partner draws are
        /// appended after the per-cloud block rather than interleaved into it.
        /// </summary>
        [TestMethod]
        public void Plan_PairChange_LeavesThePrimariesUntouched()
        {
            var paired = SkyDecorLayout.Plan(MakeRegion(), MaraeBluespire, FlatGround, null);
            var unpaired = SkyDecorLayout.Plan(MakeRegion(r => r.PairChance = 0.0f), MaraeBluespire, FlatGround, null);

            Assert.AreEqual(PrimaryFingerprint(paired), PrimaryFingerprint(unpaired));
        }

        [TestMethod]
        public void Plan_Partner_UsesADifferentColourWhenThePaletteAllows()
        {
            var checkedPartners = 0;

            for (var x = 0; x <= 0x3F; x++)
            {
                for (var y = 0; y <= 0x3F; y++)
                {
                    var lb = (ushort)((x << 8) | y);

                    foreach (var c in SkyDecorLayout.Plan(MakeRegion(), lb, FlatGround, null))
                    {
                        Assert.IsNotNull(c.Partner);
                        Assert.AreNotEqual(c.WeenieClassId, c.Partner.WeenieClassId, $"landblock 0x{lb:X4} paired a disc with its own colour");

                        checkedPartners++;
                    }
                }
            }

            Assert.IsTrue(checkedPartners > 1000, $"only {checkedPartners} partners were checked");
        }

        /// <summary>A one-colour palette has nothing else to offer, so the partner reuses the primary's colour.</summary>
        [TestMethod]
        public void Plan_SingleEntryPalette_PartnerReusesTheSameColour()
        {
            var clouds = SkyDecorLayout.Plan(MakeRegion(r => r.Palette = "1002649"), MaraeBluespire, FlatGround, null);

            Assert.IsTrue(clouds.Count > 0);
            Assert.IsTrue(clouds.All(c => c.Partner != null && c.Partner.WeenieClassId == 1002649u));
        }

        /// <summary>Every colour of a multi-entry palette must still be reachable as a partner.</summary>
        [TestMethod]
        public void Plan_PartnerColours_CoverTheWholePalette()
        {
            var seen = new HashSet<uint>();

            for (var x = 0; x <= 0x1F; x++)
            {
                for (var y = 0; y <= 0x1F; y++)
                {
                    foreach (var c in SkyDecorLayout.Plan(MakeRegion(), (ushort)((x << 8) | y), FlatGround, null))
                        seen.Add(c.Partner.WeenieClassId);
                }
            }

            CollectionAssert.AreEquivalent(
                new[] { 1002642u, 1002643u, 1002644u, 1002645u, 1002646u, 1002647u, 1002649u },
                seen.ToArray());
        }

        [TestMethod]
        public void Plan_Partner_IsSmallerAndFasterByTheConfiguredRatios()
        {
            var region = MakeRegion();

            var clouds = SkyDecorLayout.Plan(region, MaraeBluespire, FlatGround, null);

            Assert.IsTrue(clouds.Count > 0);

            foreach (var c in clouds)
            {
                Assert.AreEqual(c.Scale * region.PairScaleRatio, c.Partner.Scale, 1e-4f, "partner scale ratio");
                Assert.AreEqual(c.Spin * region.PairSpeedRatio, c.Partner.Spin, 1e-4f, "partner speed ratio");

                Assert.IsTrue(c.Partner.Scale < c.Scale, "the partner must be the smaller disc");
                Assert.IsTrue(c.Partner.Spin > c.Spin, "the smaller disc must turn faster");
            }
        }

        /// <summary>
        /// The two discs are parallel, and their CENTRES are exactly pair_gap apart along the shared disc
        /// normal - with the partner on the ground side. Measured along n rather than in Z, because that
        /// is what "gap between the planes" means once the pair is tilted.
        /// </summary>
        [TestMethod]
        public void Plan_Partner_SitsPairGapBelowAlongTheDiscNormal()
        {
            var region = MakeRegion();

            var clouds = SkyDecorLayout.Plan(region, MaraeBluespire, FlatGround, null);

            Assert.IsTrue(clouds.Count > 0);

            foreach (var c in clouds)
            {
                var p = c.Partner;

                // identical orientation = parallel discs
                Assert.AreEqual(c.AnglesW, p.AnglesW, 1e-6f);
                Assert.AreEqual(c.AnglesX, p.AnglesX, 1e-6f);
                Assert.AreEqual(c.AnglesY, p.AnglesY, 1e-6f);
                Assert.AreEqual(c.AnglesZ, p.AnglesZ, 1e-6f);
                Assert.AreEqual(c.TiltDeg, p.TiltDeg, 1e-6f);
                Assert.AreEqual(c.YawDeg, p.YawDeg, 1e-6f);

                var n = SkyDecorLayout.DiscNormal(c.YawDeg, c.TiltDeg);

                var d1 = DiscCentre(c, region.PartOffset, n);
                var d2 = DiscCentre(p, region.PartOffset, n);

                var delta = d2 - d1;

                // the whole separation lies along n ...
                Assert.AreEqual(region.PairGap, delta.Length(), 1e-3f, "distance between the disc planes");

                // ... in the +n direction, which is the ground side
                Assert.AreEqual(region.PairGap, Vector3.Dot(delta, n), 1e-3f, "the partner must be on the ground side");

                // and lower in absolute terms too, since n points down-ish at these tilts
                Assert.IsTrue(d2.Z < d1.Z, "the partner disc must hang below the primary");
            }
        }

        private static Vector3 DiscCentre(SkyDecorCloud c, float partOffset, Vector3 n)
        {
            return new Vector3(c.X, c.Y, c.OriginZ) + partOffset * c.Scale * n;
        }

        [TestMethod]
        public void DiscNormal_IsAUnitVectorPointingDownAtZeroTilt()
        {
            var flat = SkyDecorLayout.DiscNormal(137.0f, 0.0f);

            Assert.AreEqual(0.0f, flat.X, 1e-5f);
            Assert.AreEqual(0.0f, flat.Y, 1e-5f);
            Assert.AreEqual(-1.0f, flat.Z, 1e-5f);

            for (var yaw = 0.0f; yaw < 360.0f; yaw += 23.0f)
            {
                for (var tilt = 0.0f; tilt <= 45.0f; tilt += 5.0f)
                {
                    var n = SkyDecorLayout.DiscNormal(yaw, tilt);

                    Assert.AreEqual(1.0f, n.Length(), 1e-5f, $"yaw {yaw} tilt {tilt}");

                    // the vertical component is cos(180 - tilt), which is the term OriginZ uses
                    Assert.AreEqual((float)Math.Cos((180.0 - tilt) * Math.PI / 180.0), n.Z, 1e-5f);
                }
            }
        }

        /// <summary>
        /// The Height/GroundZ invariant must hold for partners too, or /sky-decor info and the height
        /// column would mean one thing for a primary and another for its partner.
        /// </summary>
        [TestMethod]
        public void Plan_PartnerOriginZ_IsConsistentWithItsOwnHeightColumn()
        {
            var region = MakeRegion();

            foreach (var c in SkyDecorLayout.Plan(region, MaraeBluespire, FlatGround, null).SelectMany(c => c.WithPartner()))
            {
                var discZ = c.OriginZ + region.PartOffset * c.Scale * (float)Math.Cos((180.0 - c.TiltDeg) * Math.PI / 180.0);

                Assert.AreEqual(c.GroundZ + c.Height, discZ, 1e-2f);
            }
        }

        /// <summary>Partners stay inside the landblock at the shipped tilt cap - the clamp never has to fire.</summary>
        [TestMethod]
        public void Plan_Partners_StayInsideTheLandblock()
        {
            for (var x = 0x1F; x <= 0x23; x++)
            {
                for (var y = 0xAE; y <= 0xB2; y++)
                {
                    var lb = (ushort)((x << 8) | y);

                    foreach (var c in SkyDecorLayout.Plan(MakeRegion(), lb, FlatGround, null).SelectMany(c => c.WithPartner()))
                    {
                        Assert.IsTrue(c.X >= 0.0f && c.X < 192.0f, $"x {c.X}");
                        Assert.IsTrue(c.Y >= 0.0f && c.Y < 192.0f, $"y {c.Y}");
                        Assert.IsTrue(c.CellX >= 0 && c.CellX <= 7);
                        Assert.IsTrue(c.CellY >= 0 && c.CellY <= 7);
                    }
                }
            }
        }

        // =======================================================================================
        // Water
        // =======================================================================================

        [TestMethod]
        public void Plan_OverWaterFalse_SkipsWaterCells()
        {
            // the western half of the block is water
            bool IsWater(int cellX, int cellY) => cellX < 4;

            var wet = SkyDecorLayout.Plan(MakeRegion(r => { r.OverWater = true; r.Density = 20.0f; }), MaraeBluespire, FlatGround, IsWater);
            var dry = SkyDecorLayout.Plan(MakeRegion(r => { r.OverWater = false; r.Density = 20.0f; }), MaraeBluespire, FlatGround, IsWater);

            Assert.IsTrue(wet.Any(c => c.CellX < 4), "the control must actually place clouds over water");
            Assert.IsFalse(dry.Any(c => c.CellX < 4), "over_water = 0 placed a cloud over water");

            // the surviving clouds are exactly the dry ones from the same draw - the water test drops
            // clouds, it does not reshuffle the block
            CollectionAssert.AreEqual(
                wet.Where(c => c.CellX >= 4).Select(c => (c.CellX, c.CellY)).ToArray(),
                dry.Select(c => (c.CellX, c.CellY)).ToArray());
        }

        [TestMethod]
        public void Plan_OverWaterTrue_IgnoresTheWaterTestEntirely()
        {
            var withCallback = SkyDecorLayout.Plan(MakeRegion(), MaraeBluespire, FlatGround, (cx, cy) => true);
            var withoutCallback = SkyDecorLayout.Plan(MakeRegion(), MaraeBluespire, FlatGround, null);

            Assert.AreEqual(Fingerprint(withoutCallback), Fingerprint(withCallback));
        }

        // =======================================================================================
        // Rebuild scheduling
        //
        // These use the REAL ActionQueue/ActionEventDelegate (both are world-free) against a stand-in
        // for Landblock's rebuild body. They cover the SCHEDULING SHAPE - that one idempotent body makes
        // ordering irrelevant - and deliberately not the Landblock wiring itself, which needs dat files
        // and a live world to construct. The matching wiring check is the /reload-landblock step in the
        // Docs/VERIFY-QUEUE.md row.
        // =======================================================================================

        /// <summary>
        /// A stand-in for Landblock's sky-decor bookkeeping: a list of "clouds" tagged by generation, a
        /// destroy that empties it, and a rebuild body shaped exactly like Landblock.RebuildSkyDecor
        /// (destroy, then spawn).
        /// </summary>
        private sealed class FakeSkyDecorBlock
        {
            private readonly List<int> objects = new List<int>();

            public int Generation;

            public int Count => objects.Count;

            public IReadOnlyList<int> Objects => objects;

            /// <summary>Landblock.RebuildSkyDecor's shape: destroy what is there, then re-derive all of it.</summary>
            public void Rebuild(int cloudsPerBlock)
            {
                objects.Clear();

                Generation++;

                for (var i = 0; i < cloudsPerBlock; i++)
                    objects.Add(Generation);
            }

            /// <summary>The shape the first version had: spawn only, never destroy. Kept as the control.</summary>
            public void SpawnOnly(int cloudsPerBlock)
            {
                Generation++;

                for (var i = 0; i < cloudsPerBlock; i++)
                    objects.Add(Generation);
            }
        }

        /// <summary>
        /// The race this guards: LandblockManager.GetLandblock adds a landblock to loadedLandblocks
        /// (LandblockManager.cs:551) BEFORE calling Init (:564), and Init spawns its work on a background
        /// Task - so /sky-decor reload can queue a rebuild for a landblock whose own first spawn has not
        /// reached the queue yet. Either order must leave exactly one generation.
        /// </summary>
        [TestMethod]
        public void SkyDecorRebuild_ReloadQueuedBeforeInitialSpawn_LeavesOneGeneration()
        {
            const int clouds = 5;

            foreach (var reloadFirst in new[] { true, false })
            {
                var block = new FakeSkyDecorBlock();
                var queue = new ActionQueue();

                // The two real call sites. They are deliberately the SAME body - that is the fix - but
                // they are written out separately here because the point being asserted is that the two
                // distinct entry points cannot fight, in either arrival order.
                var initialSpawn = new ActionEventDelegate(() => block.Rebuild(clouds));   // Landblock.SpawnSkyDecor
                var reloadRebuild = new ActionEventDelegate(() => block.Rebuild(clouds));  // Landblock.ReloadSkyDecor

                if (reloadFirst)
                {
                    queue.EnqueueAction(reloadRebuild);
                    queue.EnqueueAction(initialSpawn);
                }
                else
                {
                    queue.EnqueueAction(initialSpawn);
                    queue.EnqueueAction(reloadRebuild);
                }

                queue.RunActions();

                Assert.AreEqual(clouds, block.Count, $"reloadFirst={reloadFirst}: the block must hold exactly one generation");
                Assert.AreEqual(1, block.Objects.Distinct().Count(), $"reloadFirst={reloadFirst}: two generations are present at once");
            }
        }

        /// <summary>
        /// Control: the spawn-only shape the first version had really does double under the same
        /// schedule. Without this the test above passes for reasons unrelated to the fix.
        /// </summary>
        [TestMethod]
        public void SkyDecorRebuild_SpawnOnlyShape_WouldHaveDoubled()
        {
            const int clouds = 5;

            var block = new FakeSkyDecorBlock();
            var queue = new ActionQueue();

            queue.EnqueueAction(new ActionEventDelegate(() => block.SpawnOnly(clouds)));
            queue.EnqueueAction(new ActionEventDelegate(() => block.SpawnOnly(clouds)));

            queue.RunActions();

            Assert.AreEqual(clouds * 2, block.Count, "the control must actually double, or the fix above proves nothing");
            Assert.AreEqual(2, block.Objects.Distinct().Count());
        }

        /// <summary>
        /// A rebuild is idempotent however many times it runs, so repeated /sky-decor reloads (and a
        /// reload racing an activation racing another reload) all settle on one generation.
        /// </summary>
        [TestMethod]
        public void SkyDecorRebuild_RepeatedRebuilds_NeverAccumulate()
        {
            const int clouds = 7;

            var block = new FakeSkyDecorBlock();

            for (var round = 1; round <= 10; round++)
            {
                var queue = new ActionQueue();

                for (var i = 0; i < round; i++)
                    queue.EnqueueAction(new ActionEventDelegate(() => block.Rebuild(clouds)));

                queue.RunActions();

                Assert.AreEqual(clouds, block.Count, $"after {round} queued rebuild(s)");
                Assert.AreEqual(1, block.Objects.Distinct().Count(), $"after {round} queued rebuild(s)");
            }
        }

        // =======================================================================================
        // Region coverage
        // =======================================================================================

        [TestMethod]
        public void Covers_IsAnInclusiveRectangleInTheRegionsOwnRealm()
        {
            var region = MakeRegion();

            Assert.IsTrue(region.Covers(0x1FAE, 1), "the low corner is inside");
            Assert.IsTrue(region.Covers(0x23B2, 1), "the high corner is inside");
            Assert.IsTrue(region.Covers(0x21B0, 1));

            Assert.IsFalse(region.Covers(0x1EAE, 1), "one block west");
            Assert.IsFalse(region.Covers(0x24AE, 1), "one block east");
            Assert.IsFalse(region.Covers(0x1FAD, 1), "one block south");
            Assert.IsFalse(region.Covers(0x1FB3, 1), "one block north");

            Assert.IsFalse(region.Covers(0x21B0, 0), "realm 0 must not see a realm 1 region");
            Assert.IsFalse(region.Covers(0x21B0, 2), "another realm must not see it either");
        }

        // =======================================================================================
        // Persistence
        // =======================================================================================

        /// <summary>
        /// A Sky Rift-shaped weenie, built by hand so this needs no database (the BadSetupDidTests pattern).
        /// </summary>
        private static WorldObject MakeSkyRiftLikeObject(uint guid)
        {
            var weenie = new ACE.Entity.Models.Weenie
            {
                WeenieClassId = 1002642,
                ClassName = "ace1002642-skyriftpurple",
                WeenieType = WeenieType.Generic,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Sky Rift" } },
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)ItemType.Misc },
                    { PropertyInt.ItemUseable, (int)Usable.No },
                    { PropertyInt.PhysicsState, 0x815 },
                },
                PropertiesBool = new Dictionary<PropertyBool, bool>
                {
                    { PropertyBool.Stuck, true },
                    { PropertyBool.IgnoreCollisions, true },
                    { PropertyBool.Ethereal, true },
                    { PropertyBool.GravityStatus, false },
                    { PropertyBool.UiHidden, true },
                    { PropertyBool.IgnoreInitialClamp, true },
                },
                PropertiesFloat = new Dictionary<PropertyFloat, double>
                {
                    { PropertyFloat.DefaultScale, 25.0 },
                    { PropertyFloat.MotionSpeed, 1.0 },
                },
                PropertiesDID = new Dictionary<PropertyDataId, uint> { { PropertyDataId.Setup, 0x02001871 } },
            };

            return new GenericObject(weenie, new ObjectGuid(guid));
        }

        /// <summary>
        /// The invariant the whole feature rests on: a sky-decor object is never written to the shard.
        /// Landblock.SaveDB only saves what one of these two predicates accepts (Landblock.cs, SaveDB).
        /// </summary>
        [TestMethod]
        public void SkyDecorObject_IsNeverPersistedToTheShard()
        {
            var wo = MakeSkyRiftLikeObject(ObjectGuid.DynamicMin + 1);

            wo.IsSkyDecor = true;
            wo.TimeToRot = -1;

            Assert.IsFalse(wo.IsStaticThatShouldPersistToShard(), "a dynamic-guid decor object is not a static that persists");
            Assert.IsFalse(wo.IsDynamicThatShouldPersistToShard(), "sky decor must never be saved to the shard");
        }

        /// <summary>
        /// Control: the SAME object without the flag WOULD be saved. Without this the test above passes
        /// for reasons that have nothing to do with the guard being present.
        /// </summary>
        [TestMethod]
        public void SkyDecorObject_WithoutTheFlag_WouldHaveBeenPersisted()
        {
            var wo = MakeSkyRiftLikeObject(ObjectGuid.DynamicMin + 2);

            wo.TimeToRot = -1;

            Assert.IsTrue(wo.IsDynamicThatShouldPersistToShard(), "the control must actually be persistable, or the guard above proves nothing");
        }

        /// <summary>
        /// The flag is a runtime field, not a property: it must leave nothing in the biota that could be
        /// written out or read back.
        /// </summary>
        [TestMethod]
        public void SkyDecorFlag_LeavesNothingInTheBiota()
        {
            var wo = MakeSkyRiftLikeObject(ObjectGuid.DynamicMin + 3);

            var boolsBefore = wo.Biota.PropertiesBool == null ? 0 : wo.Biota.PropertiesBool.Count;
            var intsBefore = wo.Biota.PropertiesInt == null ? 0 : wo.Biota.PropertiesInt.Count;

            wo.IsSkyDecor = true;

            Assert.AreEqual(boolsBefore, wo.Biota.PropertiesBool == null ? 0 : wo.Biota.PropertiesBool.Count);
            Assert.AreEqual(intsBefore, wo.Biota.PropertiesInt == null ? 0 : wo.Biota.PropertiesInt.Count);
        }

        /// <summary>
        /// A decor object must not rot: it is a dynamic-guid Generic with no generator, which is exactly
        /// the shape WorldObject_Decay.IsDecayable accepts by default.
        /// </summary>
        [TestMethod]
        public void SkyDecorObject_WithTimeToRotMinusOne_IsNotDecayable()
        {
            var control = MakeSkyRiftLikeObject(ObjectGuid.DynamicMin + 4);

            Assert.IsTrue(control.IsDecayable(), "the control must actually be decayable, or TimeToRot = -1 proves nothing");

            var wo = MakeSkyRiftLikeObject(ObjectGuid.DynamicMin + 5);

            wo.TimeToRot = -1;

            Assert.IsFalse(wo.IsDecayable());
        }

        // =======================================================================================
        // No-overlap cull (min_separation)
        // =======================================================================================

        /// <summary>
        /// The Marae square, but pinned to one block or a few, and at a scale where every disc in reach
        /// conflicts with every other: 240 gives a 174 m radius, so two of them need 348 m of separation
        /// and a landblock is 192 m across. Nothing subtle can hide in a test where the geometry is that
        /// decisive.
        /// </summary>
        private static SkyDecorRegion MakeCullRegion(byte xMin, byte xMax, byte yMin, byte yMax, Action<SkyDecorRegion> tweak = null)
        {
            return MakeRegion(r =>
            {
                r.LbXMin = xMin;
                r.LbXMax = xMax;
                r.LbYMin = yMin;
                r.LbYMax = yMax;
                r.Density = 3.0f;
                r.ScaleMin = 240.0f;
                r.ScaleMax = 240.0f;
                r.MinSeparation = 1.0f;
                r.PairChance = 0.0f;

                tweak?.Invoke(r);
            });
        }

        private static List<ushort> BlocksOf(SkyDecorRegion region)
        {
            var blocks = new List<ushort>();

            for (var x = region.LbXMin; x <= region.LbXMax; x++)
                for (var y = region.LbYMin; y <= region.LbYMax; y++)
                    blocks.Add((ushort)((x << 8) | y));

            return blocks;
        }

        /// <summary>
        /// The cull rule applied with a god's-eye view of every candidate in the region at once. The
        /// per-block implementation has to agree with this exactly - that agreement IS the invariant,
        /// because a landblock only ever sees a window.
        /// </summary>
        private static List<SkyDecorCandidate> GlobalSurvivors(SkyDecorRegion region)
        {
            var all = new List<SkyDecorCandidate>();

            foreach (var block in BlocksOf(region))
                all.AddRange(SkyDecorLayout.PlanCandidates(region, block));

            return all.Where(c => !all.Any(o =>
                (o.LandblockId != c.LandblockId || o.Index != c.Index)
                && SkyDecorLayout.Beats(o, c)
                && SkyDecorLayout.Conflicts(region, c, o))).ToList();
        }

        private static string CandidateKey(SkyDecorCandidate c) => $"{c.LandblockId:X4}#{c.Index}";

        [TestMethod]
        public void Cull_OverlappingCandidatesInOneBlock_LeaveExactlyOneSurvivor()
        {
            // A region of exactly one landblock, so there are no neighbours to complicate the verdict and
            // the answer is forced: at scale 240 all three candidates conflict with each other, so only
            // the highest-priority one can survive.
            var region = MakeCullRegion(0x21, 0x21, 0xB0, 0xB0);

            var candidates = SkyDecorLayout.PlanCandidates(region, MaraeBluespire);

            Assert.IsTrue(candidates.Count >= 2, $"the test needs at least two candidates to cull, got {candidates.Count}");

            for (var i = 0; i < candidates.Count; i++)
                for (var j = i + 1; j < candidates.Count; j++)
                    Assert.IsTrue(SkyDecorLayout.Conflicts(region, candidates[i], candidates[j]), "the fixture is supposed to force every pair to conflict");

            var survivors = SkyDecorLayout.Cull(region, MaraeBluespire, candidates);

            Assert.AreEqual(1, survivors.Count);

            var expected = candidates.OrderBy(c => c.Priority).ThenBy(c => c.Index).First();

            Assert.AreEqual(expected.Index, survivors[0].Index, "the surviving candidate must be the highest-priority one");
        }

        /// <summary>
        /// The cross-block invariant, and the reason the cull generates neighbours at all: two adjacent
        /// landblocks planned INDEPENDENTLY must never both keep a disc that overlaps the other's. Nothing
        /// coordinates them at runtime - they are planned on different threads at different times - so the
        /// only thing that can make this hold is that the verdict is a pure function both sides compute.
        /// </summary>
        [TestMethod]
        public void Cull_AdjacentBlocksPlannedIndependently_NeverKeepOverlappingDiscs()
        {
            var region = MakeCullRegion(0x1F, 0x23, 0xAE, 0xB2);

            var survivors = new List<SkyDecorCandidate>();

            foreach (var block in BlocksOf(region))
                survivors.AddRange(SkyDecorLayout.Cull(region, block, SkyDecorLayout.PlanCandidates(region, block)));

            Assert.IsTrue(survivors.Count > 0, "the region must keep something, or this test proves nothing");

            for (var i = 0; i < survivors.Count; i++)
            {
                for (var j = i + 1; j < survivors.Count; j++)
                {
                    Assert.IsFalse(SkyDecorLayout.Conflicts(region, survivors[i], survivors[j]),
                        $"{CandidateKey(survivors[i])} and {CandidateKey(survivors[j])} both survived but overlap");
                }
            }
        }

        /// <summary>
        /// Per-block culling must reproduce the god's-eye answer exactly - not merely a non-overlapping
        /// subset of it. A block that culled MORE than it had to would silently thin the sky, and no
        /// overlap test would catch that.
        /// </summary>
        [TestMethod]
        public void Cull_PerBlock_MatchesTheGlobalVerdict()
        {
            var region = MakeCullRegion(0x1F, 0x23, 0xAE, 0xB2);

            var perBlock = new List<SkyDecorCandidate>();

            foreach (var block in BlocksOf(region))
                perBlock.AddRange(SkyDecorLayout.Cull(region, block, SkyDecorLayout.PlanCandidates(region, block)));

            var global = GlobalSurvivors(region);

            CollectionAssert.AreEqual(
                global.Select(CandidateKey).OrderBy(k => k, StringComparer.Ordinal).ToList(),
                perBlock.Select(CandidateKey).OrderBy(k => k, StringComparer.Ordinal).ToList());
        }

        /// <summary>
        /// A block on the region's edge sees fewer neighbours than one in the middle. It must still reach
        /// the same verdict about its own candidates, because the rule only ever looks UP the priority
        /// order and never asks whether a competitor itself survived.
        /// </summary>
        [TestMethod]
        public void Cull_EdgeBlock_AgreesWithTheGlobalVerdict()
        {
            var region = MakeCullRegion(0x1F, 0x23, 0xAE, 0xB2);

            var corner = (ushort)0x1FAE;

            var mine = SkyDecorLayout.Cull(region, corner, SkyDecorLayout.PlanCandidates(region, corner)).Select(CandidateKey).ToList();

            var theirs = GlobalSurvivors(region).Where(c => c.LandblockId == corner).Select(CandidateKey).ToList();

            CollectionAssert.AreEqual(theirs, mine);
        }

        [TestMethod]
        public void Cull_IsDeterministic()
        {
            var region = MakeCullRegion(0x1F, 0x23, 0xAE, 0xB2);

            var first = SkyDecorLayout.Cull(region, MaraeBluespire, SkyDecorLayout.PlanCandidates(region, MaraeBluespire)).Select(CandidateKey).ToList();

            for (var i = 0; i < 20; i++)
            {
                var again = SkyDecorLayout.Cull(region, MaraeBluespire, SkyDecorLayout.PlanCandidates(region, MaraeBluespire)).Select(CandidateKey).ToList();

                CollectionAssert.AreEqual(first, again);
            }
        }

        /// <summary>
        /// min_separation 0 is the off switch, and "off" has to mean bit-for-bit what the layout did
        /// before the cull existed - otherwise every region that does not want culling silently reshuffles
        /// the day this ships.
        /// </summary>
        [TestMethod]
        public void Cull_MinSeparationZero_KeepsEveryCandidateInOrder()
        {
            var region = MakeCullRegion(0x1F, 0x23, 0xAE, 0xB2, r => r.MinSeparation = 0.0f);

            foreach (var block in BlocksOf(region))
            {
                var candidates = SkyDecorLayout.PlanCandidates(region, block);

                var plan = SkyDecorLayout.PlanBlock(region, block, FlatGround, null);

                Assert.AreEqual(candidates.Count, plan.Candidates);
                Assert.AreEqual(candidates.Count, plan.Clouds.Count, $"block 0x{block:X4} lost a cloud with the cull off");
                Assert.AreEqual(0, plan.CulledBySeparation);

                for (var i = 0; i < candidates.Count; i++)
                {
                    Assert.AreEqual(candidates[i].X, plan.Clouds[i].X, 1e-6f);
                    Assert.AreEqual(candidates[i].Y, plan.Clouds[i].Y, 1e-6f);
                    Assert.AreEqual(candidates[i].Scale, plan.Clouds[i].Scale, 1e-6f);
                    Assert.AreEqual(candidates[i].WeenieClassId, plan.Clouds[i].WeenieClassId);
                }
            }
        }

        /// <summary>
        /// The default fixture has MinSeparation 0, so this is the whole existing suite's baseline: turning
        /// the cull off must give back the layout every other test in this file asserts against.
        /// </summary>
        [TestMethod]
        public void Cull_MinSeparationZero_ReproducesTheUnculledLayoutOfTheDefaultFixture()
        {
            var off = MakeRegion(r => r.MinSeparation = 0.0f);
            var unset = MakeRegion();

            Assert.AreEqual(
                Fingerprint(SkyDecorLayout.Plan(unset, MaraeBluespire, FlatGround, null)),
                Fingerprint(SkyDecorLayout.Plan(off, MaraeBluespire, FlatGround, null)));
        }

        /// <summary>
        /// The search window has to be DERIVED, not fixed at the 8 adjacents. At scale 240 a conflict
        /// reaches 348 m, which is nearly two landblocks, so a 3x3 window would let a genuinely
        /// overlapping pair through while still reporting the region as clean.
        /// </summary>
        [TestMethod]
        public void NeighbourBlockRadius_GrowsWithTheConflictDistanceAndIsCapped()
        {
            Assert.AreEqual(0, SkyDecorLayout.NeighbourBlockRadius(MakeRegion(r => r.MinSeparation = 0.0f)), "off means no search at all");

            // 2 * 0.725 * 60 = 87 m, well inside one 192 m block, but never less than one block out.
            Assert.AreEqual(1, SkyDecorLayout.NeighbourBlockRadius(MakeRegion(r => { r.MinSeparation = 1.0f; r.ScaleMax = 60.0f; })));

            // 2 * 0.725 * 240 = 348 m -> ceil(348 / 192) = 2.
            Assert.AreEqual(2, SkyDecorLayout.NeighbourBlockRadius(MakeRegion(r => { r.MinSeparation = 1.0f; r.ScaleMax = 240.0f; })));

            // Capped, so a nonsense row cannot make one landblock activation plan hundreds of blocks.
            Assert.AreEqual(SkyDecorLayout.MaxNeighbourBlockRadius, SkyDecorLayout.NeighbourBlockRadius(MakeRegion(r => { r.MinSeparation = 20.0f; r.ScaleMax = 240.0f; })));
        }

        /// <summary>
        /// Priority must be a STRICT TOTAL order. If two candidates could each fail to beat the other,
        /// both would cull themselves and the pair would vanish; if either could beat itself, everything
        /// would vanish.
        /// </summary>
        [TestMethod]
        public void Beats_IsAStrictTotalOrder()
        {
            var region = MakeCullRegion(0x1F, 0x23, 0xAE, 0xB2);

            var all = new List<SkyDecorCandidate>();

            foreach (var block in BlocksOf(region))
                all.AddRange(SkyDecorLayout.PlanCandidates(region, block));

            foreach (var a in all)
            {
                Assert.IsFalse(SkyDecorLayout.Beats(a, a), "no candidate may outrank itself");

                foreach (var b in all)
                {
                    if (ReferenceEquals(a, b))
                        continue;

                    Assert.AreNotEqual(SkyDecorLayout.Beats(a, b), SkyDecorLayout.Beats(b, a),
                        $"{CandidateKey(a)} vs {CandidateKey(b)} is not a strict order");
                }
            }
        }

        /// <summary>
        /// Priority must NOT depend on the region's database id. `id` is an autoincrement surrogate that
        /// changed 1 -> 2 the second time this unit's SQL was applied locally; if the cull keyed on it,
        /// every re-apply would reshuffle which discs survive across the whole region for no content
        /// reason at all.
        /// </summary>
        [TestMethod]
        public void Cull_IsUnaffectedByTheRegionsDatabaseId()
        {
            var first = MakeCullRegion(0x1F, 0x23, 0xAE, 0xB2, r => r.Id = 1);
            var second = MakeCullRegion(0x1F, 0x23, 0xAE, 0xB2, r => r.Id = 4242);

            CollectionAssert.AreEqual(
                SkyDecorLayout.Cull(first, MaraeBluespire, SkyDecorLayout.PlanCandidates(first, MaraeBluespire)).Select(CandidateKey).ToList(),
                SkyDecorLayout.Cull(second, MaraeBluespire, SkyDecorLayout.PlanCandidates(second, MaraeBluespire)).Select(CandidateKey).ToList());
        }

        [TestMethod]
        public void PlanBlock_Counts_AlwaysAddUp()
        {
            var region = MakeCullRegion(0x1F, 0x23, 0xAE, 0xB2, r => r.OverWater = false);

            foreach (var block in BlocksOf(region))
            {
                var plan = SkyDecorLayout.PlanBlock(region, block, FlatGround, (cx, cy) => cx == cy);

                Assert.AreEqual(plan.Candidates, plan.Clouds.Count + plan.CulledBySeparation + plan.CulledByWater,
                    $"block 0x{block:X4} loses count");
            }
        }

        /// <summary>
        /// The 5x5 Bluespire square at min_separation 1.0 - what the region shipped as in round 3, kept
        /// as a golden because it is the reference point the "no two discs may overlap" setting was
        /// measured at. The region has since grown to the whole island at 0.5; see
        /// MaraeIsland_AsShipped_HasAKnownSurvivorTotal for the current row.
        /// </summary>
        [TestMethod]
        public void MaraeFiveBySquare_AtSeparationOne_HasAKnownSurvivorTotal()
        {
            var region = MakeRegion(r =>
            {
                r.Version = 3;
                r.Density = 2.5f;
                r.ScaleMin = 96.0f;
                r.ScaleMax = 240.0f;
                r.MinSeparation = 1.0f;
                r.Rig = SkyDecorLayout.RigMast;
            });

            var candidates = 0;
            var clouds = 0;
            var objects = 0;

            foreach (var block in BlocksOf(region))
            {
                var plan = SkyDecorLayout.PlanBlock(region, block, FlatGround, null);

                candidates += plan.Candidates;
                clouds += plan.Clouds.Count;
                objects += plan.Clouds.Sum(c => c.Partner != null ? 2 : 1);
            }

            Assert.AreEqual(64, candidates, "candidate total (25 blocks at density 2.5)");
            Assert.AreEqual(9, clouds, "surviving cloud total");
            Assert.AreEqual(18, objects, "object total (every cloud is a pair)");
        }

        /// <summary>
        /// Survivor count is NOT monotonic in density once a region is separation-bound, and that reads
        /// as a bug the first time someone turns the dial the wrong way. Pinned here so it is a documented
        /// property rather than a surprise: every extra candidate is also an extra SUPPRESSOR, and the
        /// cull rule counts a suppressor that was itself culled.
        /// </summary>
        [TestMethod]
        public void Cull_WhenSeparationBound_MoreDensityCanMeanFewerClouds()
        {
            SkyDecorRegion AtDensity(float density) => MakeRegion(r =>
            {
                r.Version = 3;
                r.Density = density;
                r.ScaleMin = 96.0f;
                r.ScaleMax = 240.0f;
                r.MinSeparation = 1.0f;
                r.Rig = SkyDecorLayout.RigMast;
            });

            int Survivors(SkyDecorRegion region) =>
                BlocksOf(region).Sum(b => SkyDecorLayout.PlanBlock(region, b, FlatGround, null).Clouds.Count);

            var thin = Survivors(AtDensity(2.5f));
            var thick = Survivors(AtDensity(5.0f));

            Assert.AreEqual(9, thin);
            Assert.AreEqual(7, thick);

            Assert.IsTrue(thick < thin, "the whole point of this test is that the thicker region holds fewer discs");
        }

        /// <summary>
        /// The dial that DOES add clouds to a separation-bound region: smaller discs, or a smaller
        /// min_separation. The row header tells the owner to reach for these, so they are pinned too.
        /// </summary>
        [TestMethod]
        public void Cull_SmallerDiscsOrSmallerSeparation_AddClouds()
        {
            SkyDecorRegion Tuned(float scaleMin, float scaleMax, float minSep) => MakeRegion(r =>
            {
                r.Version = 3;
                r.Density = 2.5f;
                r.ScaleMin = scaleMin;
                r.ScaleMax = scaleMax;
                r.MinSeparation = minSep;
                r.Rig = SkyDecorLayout.RigMast;
            });

            int Survivors(SkyDecorRegion region) =>
                BlocksOf(region).Sum(b => SkyDecorLayout.PlanBlock(region, b, FlatGround, null).Clouds.Count);

            var shipped = Survivors(Tuned(96.0f, 240.0f, 1.0f));

            Assert.AreEqual(9, shipped);
            Assert.AreEqual(22, Survivors(Tuned(96.0f, 240.0f, 0.5f)), "halving min_separation");
            Assert.AreEqual(17, Survivors(Tuned(60.0f, 150.0f, 1.0f)), "smaller discs at the same separation");
            Assert.AreEqual(64, Survivors(Tuned(96.0f, 240.0f, 0.0f)), "the cull off keeps every candidate");
        }

        // =======================================================================================
        // Runtime overrides (/sky-decor set | show | sql)
        // =======================================================================================

        /// <summary>
        /// The override store is process-wide static state, so every test in this section starts by
        /// emptying it. Without that, one test's leftover override decides another test's result and the
        /// failure lands on whichever test happens to run second.
        /// </summary>
        private static SkyDecorRegion FreshOverrideFixture()
        {
            SkyDecorOverrides.ClearAll();

            return MakeRegion(r =>
            {
                r.Name = "override_test";
                r.Version = 3;
                r.Density = 2.5f;
                r.ScaleMin = 96.0f;
                r.ScaleMax = 240.0f;
                r.MinSeparation = 1.0f;
                r.Rig = SkyDecorLayout.RigMast;
            });
        }

        [TestMethod]
        public void Overrides_Set_ChangesTheEffectiveValueAndMarksTheColumn()
        {
            var db = FreshOverrideFixture();

            Assert.IsFalse(SkyDecorOverrides.AnyActive);

            Assert.IsTrue(SkyDecorOverrides.TryApply(db, "min_separation", "0.5", out var updated, out var error), error);

            SkyDecorOverrides.Store(updated, "min_separation");

            Assert.IsTrue(SkyDecorOverrides.AnyActive);
            Assert.IsTrue(SkyDecorOverrides.IsOverridden(db.Name, "min_separation"));
            Assert.IsFalse(SkyDecorOverrides.IsOverridden(db.Name, "density"));

            var effective = SkyDecorOverrides.Effective(new List<SkyDecorRegion> { db });

            Assert.AreEqual(1, effective.Count);
            Assert.AreEqual(0.5f, effective[0].MinSeparation, 1e-6f);
            Assert.AreEqual(2.5f, effective[0].Density, 1e-6f, "an untouched column must keep its database value");

            SkyDecorOverrides.ClearAll();
        }

        /// <summary>
        /// The cached row is shared with landblock threads that may be mid-rebuild, so an override must
        /// never write into it. A test rather than a comment because the failure mode - a value that
        /// survives a reload - would look like a caching bug anywhere but here.
        /// </summary>
        [TestMethod]
        public void Overrides_NeverMutateTheCachedDatabaseRow()
        {
            var db = FreshOverrideFixture();

            Assert.IsTrue(SkyDecorOverrides.TryApply(db, "scale_max", "111", out var updated, out var error), error);

            SkyDecorOverrides.Store(updated, "scale_max");

            Assert.AreEqual(240.0f, db.ScaleMax, 1e-6f, "the database row was mutated in place");
            Assert.AreEqual(111.0f, updated.ScaleMax, 1e-6f);
            Assert.AreNotSame(db, updated);

            SkyDecorOverrides.ClearAll();
        }

        [TestMethod]
        public void Overrides_ClearAll_DropsMarksAndRestoresTheDatabaseValues()
        {
            var db = FreshOverrideFixture();

            Assert.IsTrue(SkyDecorOverrides.TryApply(db, "density", "6", out var updated, out _));

            SkyDecorOverrides.Store(updated, "density");

            Assert.AreEqual(1, SkyDecorOverrides.ClearAll(), "one region had an override");

            Assert.IsFalse(SkyDecorOverrides.AnyActive);
            Assert.IsFalse(SkyDecorOverrides.IsOverridden(db.Name, "density"));
            Assert.AreEqual(0, SkyDecorOverrides.OverriddenColumns(db.Name).Count);

            var effective = SkyDecorOverrides.Effective(new List<SkyDecorRegion> { db });

            Assert.AreSame(db, effective[0], "with no overrides the caller's own list is handed straight back");
            Assert.AreEqual(2.5f, effective[0].Density, 1e-6f);

            Assert.AreEqual(0, SkyDecorOverrides.ClearAll(), "clearing twice reports nothing the second time");
        }

        [TestMethod]
        public void Overrides_SettingTheSameRegionTwice_KeepsBothColumns()
        {
            var db = FreshOverrideFixture();

            Assert.IsTrue(SkyDecorOverrides.TryApply(db, "density", "4", out var first, out _));
            SkyDecorOverrides.Store(first, "density");

            // The second edit starts from the EFFECTIVE region, which is how the command handler does it -
            // starting from the database row again would silently discard the first override.
            var effective = SkyDecorOverrides.Effective(new List<SkyDecorRegion> { db })[0];

            Assert.IsTrue(SkyDecorOverrides.TryApply(effective, "rig", "inverted", out var second, out _));
            SkyDecorOverrides.Store(second, "rig");

            var final = SkyDecorOverrides.Effective(new List<SkyDecorRegion> { db })[0];

            Assert.AreEqual(4.0f, final.Density, 1e-6f);
            Assert.AreEqual(SkyDecorLayout.RigInverted, final.Rig);

            CollectionAssert.AreEquivalent(new[] { "density", "rig" }, SkyDecorOverrides.OverriddenColumns(db.Name));

            SkyDecorOverrides.ClearAll();
        }

        /// <summary>
        /// The database cache loads enabled rows only, so `enabled` is a one-way switch at runtime: it can
        /// take a region out of the effective set, and nothing here can put a disabled one back.
        /// </summary>
        [TestMethod]
        public void Overrides_EnabledZero_RemovesTheRegionFromTheEffectiveSet()
        {
            var db = FreshOverrideFixture();

            Assert.IsTrue(SkyDecorOverrides.TryApply(db, "enabled", "0", out var updated, out var error), error);

            SkyDecorOverrides.Store(updated, "enabled");

            Assert.AreEqual(0, SkyDecorOverrides.Effective(new List<SkyDecorRegion> { db }).Count);

            SkyDecorOverrides.ClearAll();

            Assert.AreEqual(1, SkyDecorOverrides.Effective(new List<SkyDecorRegion> { db }).Count);
        }

        [TestMethod]
        public void Overrides_UnknownColumn_IsRejectedAndNamesTheAlternatives()
        {
            var db = FreshOverrideFixture();

            Assert.IsFalse(SkyDecorOverrides.IsColumn("mini_separation"));

            Assert.IsFalse(SkyDecorOverrides.TryApply(db, "mini_separation", "1", out var updated, out var error));

            Assert.IsNull(updated);
            Assert.IsTrue(error.Contains("unknown column"), error);
            Assert.IsTrue(error.Contains("min_separation"), "the error must list the real columns: " + error);

            Assert.IsFalse(SkyDecorOverrides.AnyActive, "a rejected edit must leave nothing behind");
        }

        [TestMethod]
        public void Overrides_Rig_AcceptsOnlyTheTwoRigs()
        {
            var db = FreshOverrideFixture();

            Assert.IsTrue(SkyDecorOverrides.TryApply(db, "rig", "MAST", out var mast, out var error), error);
            Assert.AreEqual(SkyDecorLayout.RigMast, mast.Rig, "the value is normalised to lower case");

            Assert.IsTrue(SkyDecorOverrides.TryApply(db, "rig", "inverted", out var inverted, out error), error);
            Assert.AreEqual(SkyDecorLayout.RigInverted, inverted.Rig);

            Assert.IsFalse(SkyDecorOverrides.TryApply(db, "rig", "sideways", out var bad, out error));
            Assert.IsNull(bad);
            Assert.IsTrue(error.Contains("mast"), error);
        }

        [TestMethod]
        public void Overrides_InvertedMinMaxPair_IsRejected()
        {
            var db = FreshOverrideFixture();

            // scale_min 96, scale_max 240 -> pushing the minimum above the maximum must be refused rather
            // than quietly sorted. The layout math tolerates a swapped pair, which is exactly why the
            // refusal has to happen here: nothing downstream would ever complain.
            Assert.IsFalse(SkyDecorOverrides.TryApply(db, "scale_min", "300", out var bad, out var error));
            Assert.IsNull(bad);
            Assert.IsTrue(error.Contains("scale_min"), error);

            Assert.IsFalse(SkyDecorOverrides.TryApply(db, "height_max", "-5", out bad, out error));
            Assert.IsTrue(error.Contains("height_min"), error);

            Assert.IsFalse(SkyDecorOverrides.TryApply(db, "spin_min", "9", out bad, out error));
            Assert.IsTrue(error.Contains("spin_min"), error);

            // ... and the legal direction still works.
            Assert.IsTrue(SkyDecorOverrides.TryApply(db, "scale_min", "50", out var good, out error), error);
            Assert.AreEqual(50.0f, good.ScaleMin, 1e-6f);
        }

        [TestMethod]
        public void Overrides_Scale_AcceptsUpTo100000RefusesAbove()
        {
            var db = FreshOverrideFixture();

            Assert.IsTrue(SkyDecorOverrides.TryApply(db, "scale_max", "100000", out var good, out var error), error);
            Assert.AreEqual(100000.0f, good.ScaleMax, 1e-3f);

            Assert.IsFalse(SkyDecorOverrides.TryApply(db, "scale_max", "100001", out var bad, out error));
            Assert.IsNull(bad);
            Assert.IsTrue(error.Contains("outside"), error);

            // scale_min 100000 needs a baseline whose scale_max is already >= 100000, or the min<=max
            // pairing check (not the range check under test) would refuse it first - use the region
            // from the scale_max=100000 call above rather than the fixture's scale_max 240.
            Assert.IsTrue(SkyDecorOverrides.TryApply(good, "scale_min", "100000", out var minGood, out error), error);
            Assert.AreEqual(100000.0f, minGood.ScaleMin, 1e-3f);

            Assert.IsFalse(SkyDecorOverrides.TryApply(good, "scale_min", "100001", out bad, out error));
            Assert.IsNull(bad);
            Assert.IsTrue(error.Contains("outside"), error);
        }

        [TestMethod]
        public void Overrides_Translucency_AcceptsInRangeRefusesOutOfRange()
        {
            var db = FreshOverrideFixture();

            Assert.IsTrue(SkyDecorOverrides.TryApply(db, "translucency_max", "0.4", out var good, out var error), error);
            Assert.AreEqual(0.4f, good.TranslucencyMax, 1e-6f);

            Assert.IsFalse(SkyDecorOverrides.TryApply(db, "translucency_max", "1.5", out var bad, out error));
            Assert.IsNull(bad);
            Assert.IsTrue(error.Contains("outside"), error);

            Assert.IsFalse(SkyDecorOverrides.TryApply(db, "translucency_max", "-0.2", out bad, out error));
            Assert.IsNull(bad);
            Assert.IsTrue(error.Contains("outside"), error);
        }

        [TestMethod]
        public void Overrides_TranslucencyInvertedMinMaxPair_IsRejected()
        {
            var db = FreshOverrideFixture();

            Assert.IsTrue(SkyDecorOverrides.TryApply(db, "translucency_max", "0.3", out var withMax, out var error), error);

            Assert.IsFalse(SkyDecorOverrides.TryApply(withMax, "translucency_min", "0.5", out var bad, out error));
            Assert.IsNull(bad);
            Assert.IsTrue(error.Contains("translucency_min"), error);
            Assert.IsTrue(error.Contains("translucency_max"), error);
        }

        [TestMethod]
        public void Overrides_Palette_MustParseToAPickableEntry()
        {
            var db = FreshOverrideFixture();

            Assert.IsTrue(SkyDecorOverrides.TryApply(db, "palette", "1002642,1002649:3", out var good, out var error), error);
            Assert.AreEqual(2, SkyDecorLayout.ParsePalette(good.Palette).Count);

            Assert.IsFalse(SkyDecorOverrides.TryApply(db, "palette", "not-a-wcid", out var bad, out error));
            Assert.IsNull(bad);

            Assert.IsFalse(SkyDecorOverrides.TryApply(db, "palette", "", out bad, out error));

            // Parses, but every weight is zero, so nothing could ever be picked and the region would go
            // silently empty. Caught here rather than discovered by flying over it.
            Assert.IsFalse(SkyDecorOverrides.TryApply(db, "palette", "1002642:0,1002643:0", out bad, out error));
            Assert.IsTrue(error.Contains("weight"), error);
        }

        [TestMethod]
        public void Overrides_Booleans_AcceptTheUsualSpellings()
        {
            var db = FreshOverrideFixture();

            foreach (var yes in new[] { "1", "true", "TRUE", "yes", "on" })
            {
                Assert.IsTrue(SkyDecorOverrides.TryApply(db, "over_water", yes, out var r, out var error), $"{yes}: {error}");
                Assert.IsTrue(r.OverWater, yes);
            }

            foreach (var no in new[] { "0", "false", "False", "no", "off" })
            {
                Assert.IsTrue(SkyDecorOverrides.TryApply(db, "over_water", no, out var r, out var error), $"{no}: {error}");
                Assert.IsFalse(r.OverWater, no);
            }

            Assert.IsFalse(SkyDecorOverrides.TryApply(db, "over_water", "maybe", out var bad, out var err));
            Assert.IsNull(bad);
            Assert.IsTrue(err.Contains("boolean"), err);
        }

        /// <summary>
        /// Numbers are parsed with the invariant culture on purpose. A value typed in the client has to
        /// mean the same thing on every machine, and the SQL this command emits has to paste into a .sql
        /// file - a locale that reads "2,5" as two-and-a-half would break both, and break them silently.
        /// </summary>
        [TestMethod]
        public void Overrides_Numbers_ParseInvariantCultureOnly()
        {
            var db = FreshOverrideFixture();

            Assert.IsTrue(SkyDecorOverrides.TryApply(db, "density", "2.75", out var good, out var error), error);
            Assert.AreEqual(2.75f, good.Density, 1e-6f);

            Assert.IsFalse(SkyDecorOverrides.TryApply(db, "density", "2,75", out var bad, out error), "a comma decimal must be refused, not silently read as 275");
            Assert.IsNull(bad);

            Assert.IsFalse(SkyDecorOverrides.TryApply(db, "density", "lots", out bad, out error));
            Assert.IsFalse(SkyDecorOverrides.TryApply(db, "density", "-1", out bad, out error), "a negative density is out of range");

            Assert.IsTrue(SkyDecorOverrides.TryApply(db, "seed", "20260821", out var seeded, out error), error);
            Assert.AreEqual(20260821, seeded.Seed);

            Assert.IsFalse(SkyDecorOverrides.TryApply(db, "version", "3.5", out bad, out error), "version is a whole number");
        }

        [TestMethod]
        public void Overrides_Columns_CoverTheDocumentedSet()
        {
            var expected = new[]
            {
                "enabled", "seed", "version", "density", "palette",
                "scale_min", "scale_max", "height_min", "height_max", "tilt_max_deg",
                "spin_min", "spin_max", "part_offset", "over_water",
                "pair_chance", "pair_scale_ratio", "pair_gap", "pair_speed_ratio",
                "min_separation", "translucency_min", "translucency_max", "rig",
            };

            CollectionAssert.AreEquivalent(expected, SkyDecorOverrides.Columns.ToArray());
        }

        /// <summary>
        /// Splits `a=1, b='x,y', c=b'1'` on the separating commas only. Written out because a palette
        /// value contains commas of its own, and a naive split would tear it in half and make the
        /// round-trip test pass for the wrong reason.
        /// </summary>
        private static List<string> SplitSqlAssignments(string body)
        {
            var parts = new List<string>();
            var current = new System.Text.StringBuilder();
            var inQuotes = false;

            foreach (var c in body)
            {
                if (c == '\'')
                    inQuotes = !inQuotes;

                if (c == ',' && !inQuotes)
                {
                    parts.Add(current.ToString().Trim());
                    current.Clear();
                    continue;
                }

                current.Append(c);
            }

            if (current.Length > 0)
                parts.Add(current.ToString().Trim());

            return parts;
        }

        private static string SqlLiteralToCommandValue(string literal)
        {
            if (literal.StartsWith("b'"))
                return literal.Substring(2).TrimEnd('\'');

            if (literal.StartsWith("'"))
                return literal.Substring(1, literal.Length - 2).Replace("''", "'");

            return literal;
        }

        /// <summary>
        /// The whole point of `/sky-decor sql` is that the statement it prints reproduces the sky it was
        /// printed from. Feeding every value it emitted back through the same parser `set` uses is the
        /// closest thing to that guarantee that can be checked without a database.
        /// </summary>
        [TestMethod]
        public void Overrides_Sql_RoundTripsThroughTheSetParsers()
        {
            var source = FreshOverrideFixture();

            source.PairGap = 0.6f;
            source.PairSpeedRatio = 1.3333f;
            source.PartOffset = 0.455f;
            source.OverWater = true;

            var sql = SkyDecorOverrides.BuildUpdateSql(source);

            Assert.IsTrue(sql.StartsWith("UPDATE sky_decor_region SET "), sql);
            Assert.IsTrue(sql.EndsWith($"WHERE name='{source.Name}';"), sql);

            var body = sql.Substring("UPDATE sky_decor_region SET ".Length);
            body = body.Substring(0, body.IndexOf(" WHERE name=", StringComparison.Ordinal));

            var assignments = SplitSqlAssignments(body);

            Assert.AreEqual(SkyDecorOverrides.Columns.Count, assignments.Count, "every tunable must appear exactly once");

            // Start from a region whose values are all DIFFERENT, so a column the statement forgot to
            // carry shows up as a mismatch rather than passing by coincidence.
            //
            // The ranges start WIDE OPEN rather than merely different, because the assignments are applied
            // one at a time in the statement's own order and the min<=max validation runs on every one of
            // them. A narrow starting range would reject `scale_min=96` for exceeding a scale_max that has
            // not been assigned yet - a false failure about ordering, not about the round trip.
            var target = MakeRegion(r =>
            {
                r.Name = source.Name;
                r.Seed = 1;
                r.Version = 1;
                r.Density = 1.0f;
                r.ScaleMin = 0.001f;
                r.ScaleMax = 10000.0f;
                r.HeightMin = -1000.0f;
                r.HeightMax = 10000.0f;
                r.TiltMaxDeg = 0.0f;
                r.SpinMin = 0.0f;
                r.SpinMax = 100.0f;
                r.PartOffset = 1.0f;
                r.OverWater = false;
                r.PairChance = 0.0f;
                r.PairScaleRatio = 1.0f;
                r.PairGap = 0.0f;
                r.PairSpeedRatio = 1.0f;
                r.MinSeparation = 0.0f;
                r.Rig = SkyDecorLayout.RigInverted;
                r.Palette = "1002642";
            });

            foreach (var assignment in assignments)
            {
                var split = assignment.IndexOf('=');

                var column = assignment.Substring(0, split);
                var literal = assignment.Substring(split + 1);

                Assert.IsTrue(SkyDecorOverrides.IsColumn(column), $"'{column}' is not a settable column");

                // Applied one at a time onto the running result, exactly as a tuning session would.
                Assert.IsTrue(SkyDecorOverrides.TryApply(target, column, SqlLiteralToCommandValue(literal), out var next, out var error),
                    $"{column}={literal}: {error}");

                target = next;
            }

            foreach (var column in SkyDecorOverrides.Columns)
                Assert.AreEqual(SkyDecorOverrides.Read(source, column), SkyDecorOverrides.Read(target, column), $"column {column} did not survive the round trip");
        }

        /// <summary>
        /// A region whose rig column has never been written reads and emits as 'inverted', so a row that
        /// predates the mast rig round-trips into an explicit value rather than into NULL.
        /// </summary>
        [TestMethod]
        public void Overrides_Sql_RendersAnUnsetRigAsInverted()
        {
            SkyDecorOverrides.ClearAll();

            var region = MakeRegion(r => { r.Name = "unset_rig"; r.Rig = null; });

            Assert.AreEqual(SkyDecorLayout.RigInverted, SkyDecorOverrides.Read(region, "rig"));

            Assert.IsTrue(SkyDecorOverrides.BuildUpdateSql(region).Contains($"rig='{SkyDecorLayout.RigInverted}'"));
        }

        /// <summary>
        /// An override has to reach the layout, not just the display. This is the whole feature in one
        /// assertion: the same block planned from the effective region gives a different sky.
        /// </summary>
        [TestMethod]
        public void Overrides_ReachTheLayoutMath()
        {
            var db = FreshOverrideFixture();

            int Total(SkyDecorRegion r) => BlocksOf(r).Sum(b => SkyDecorLayout.PlanBlock(r, b, FlatGround, null).Clouds.Count);

            var before = Total(db);

            Assert.IsTrue(SkyDecorOverrides.TryApply(db, "min_separation", "0", out var updated, out var error), error);

            SkyDecorOverrides.Store(updated, "min_separation");

            var effective = SkyDecorOverrides.Effective(new List<SkyDecorRegion> { db })[0];

            var after = Total(effective);

            Assert.AreEqual(9, before, "the shipped values keep 9 of 64 candidates");
            Assert.AreEqual(64, after, "turning the cull off keeps every candidate");

            SkyDecorOverrides.ClearAll();
        }

        // =======================================================================================
        // Colour names
        // =======================================================================================

        /// <summary>
        /// The map, asserted against the weenie FILE NAMES it was read from
        /// (Content/sql/weenies/10026NN Sky Rift (Colour).sql). A wrong id here fails nowhere at
        /// runtime - it just puts the wrong colour in the sky and looks like a rendering problem - so it
        /// is worth pinning the pairs literally rather than trusting the table to describe itself.
        /// </summary>
        [TestMethod]
        public void Colours_MapNamesToTheDocumentedWcids()
        {
            var expected = new Dictionary<string, uint>
            {
                { "purple", 1002642 },
                { "blue",   1002643 },
                { "yellow", 1002644 },
                { "green",  1002645 },
                { "orange", 1002646 },
                { "red",    1002647 },
                { "white",  1002648 },
                { "grey",   1002649 },
                { "violet", 1005750 },
                { "shadow", 1005751 },
            };

            Assert.AreEqual(expected.Count, SkyDecorColours.All.Count, "a colour was added or removed without updating this test");

            foreach (var pair in expected)
            {
                var colour = SkyDecorColours.ByName(pair.Key);

                Assert.IsNotNull(colour, pair.Key);
                Assert.AreEqual(pair.Value, colour.WeenieClassId, pair.Key);
                Assert.AreEqual(pair.Key, SkyDecorColours.ByWcid(pair.Value).Name, "the reverse lookup must agree");
            }

            Assert.IsTrue(SkyDecorColours.ByName("white").Retired, "white is retired from lineups");
            Assert.IsFalse(SkyDecorColours.ByName("purple").Retired);
            Assert.IsFalse(SkyDecorColours.ByName("violet").Retired);
            Assert.IsFalse(SkyDecorColours.ByName("shadow").Retired);
        }

        [TestMethod]
        public void Colours_NameLookup_IsCaseInsensitiveAndAcceptsTheGrayAlias()
        {
            Assert.AreEqual(1002642u, SkyDecorColours.ByName("PURPLE").WeenieClassId);
            Assert.AreEqual(1002642u, SkyDecorColours.ByName("Purple").WeenieClassId);
            Assert.AreEqual(1002642u, SkyDecorColours.ByName("  purple  ").WeenieClassId);

            Assert.AreEqual(1002649u, SkyDecorColours.ByName("grey").WeenieClassId);
            Assert.AreEqual(1002649u, SkyDecorColours.ByName("gray").WeenieClassId, "the American spelling is accepted");
            Assert.AreEqual(1002649u, SkyDecorColours.ByName("GRAY").WeenieClassId);

            Assert.AreEqual("grey", SkyDecorColours.ByWcid(1002649).Name, "but only the canonical spelling is ever printed");

            Assert.IsNull(SkyDecorColours.ByName("chartreuse"));
            Assert.IsNull(SkyDecorColours.ByName(null));
            Assert.IsNull(SkyDecorColours.ByName("  "));
        }

        [TestMethod]
        public void ParsePalette_AcceptsColourNames()
        {
            var entries = SkyDecorLayout.ParsePalette("purple,red,orange");

            CollectionAssert.AreEqual(new uint[] { 1002642, 1002647, 1002646 }, entries.Select(e => e.WeenieClassId).ToArray());
            CollectionAssert.AreEqual(new[] { 1, 1, 1 }, entries.Select(e => e.Weight).ToArray());
        }

        [TestMethod]
        public void ParsePalette_AcceptsWeightsOnNames()
        {
            var entries = SkyDecorLayout.ParsePalette("purple:3, red:2 ,orange");

            CollectionAssert.AreEqual(new uint[] { 1002642, 1002647, 1002646 }, entries.Select(e => e.WeenieClassId).ToArray());
            CollectionAssert.AreEqual(new[] { 3, 2, 1 }, entries.Select(e => e.Weight).ToArray());
        }

        /// <summary>
        /// Names and raw wcids are freely mixable, which matters because rows written before the names
        /// existed keep working and can be migrated one token at a time.
        /// </summary>
        [TestMethod]
        public void ParsePalette_MixesNamesAndRawWcids()
        {
            var entries = SkyDecorLayout.ParsePalette("purple,1002647:2,ORANGE");

            CollectionAssert.AreEqual(new uint[] { 1002642, 1002647, 1002646 }, entries.Select(e => e.WeenieClassId).ToArray());
            CollectionAssert.AreEqual(new[] { 1, 2, 1 }, entries.Select(e => e.Weight).ToArray());
        }

        [TestMethod]
        public void ParsePalette_UnknownName_IsDroppedAndReportedWithTheNameList()
        {
            var problems = new List<string>();

            var entries = SkyDecorLayout.ParsePalette("purple,chartreuse,red", problems);

            CollectionAssert.AreEqual(new uint[] { 1002642, 1002647 }, entries.Select(e => e.WeenieClassId).ToArray());

            Assert.AreEqual(1, problems.Count);
            Assert.IsTrue(problems[0].Contains("chartreuse"), problems[0]);
            Assert.IsTrue(problems[0].Contains("purple"), "the report must list the valid names: " + problems[0]);
        }

        /// <summary>
        /// White still parses and still spawns - the weenie was kept, only taken out of lineups - so it is
        /// a WARNING and not a rejection, by either spelling of the token.
        /// </summary>
        [TestMethod]
        public void ParsePalette_White_IsAcceptedWithARetiredWarning()
        {
            foreach (var token in new[] { "white", "WHITE", "1002648" })
            {
                var problems = new List<string>();

                var entries = SkyDecorLayout.ParsePalette($"purple,{token}", problems);

                CollectionAssert.AreEqual(new uint[] { 1002642, 1002648 }, entries.Select(e => e.WeenieClassId).ToArray(), token);

                Assert.AreEqual(1, problems.Count, token);
                Assert.IsTrue(problems[0].Contains("pixelated"), $"{token}: {problems[0]}");
            }

            // A palette with no retired colour in it says nothing at all.
            var quiet = new List<string>();

            SkyDecorLayout.ParsePalette("purple,red,orange", quiet);

            Assert.AreEqual(0, quiet.Count);
        }

        [TestMethod]
        public void ParsePalette_BadWeight_IsDroppedAndReported()
        {
            var problems = new List<string>();

            var entries = SkyDecorLayout.ParsePalette("purple:lots,red", problems);

            CollectionAssert.AreEqual(new uint[] { 1002647 }, entries.Select(e => e.WeenieClassId).ToArray());
            Assert.AreEqual(1, problems.Count);
            Assert.IsTrue(problems[0].Contains("weight"), problems[0]);
        }

        [TestMethod]
        public void Colours_Format_RendersNamesAndKeepsOnlyNonDefaultWeights()
        {
            Assert.AreEqual("purple,red:2,orange", SkyDecorColours.Format("1002642,1002647:2,1002646"));
            Assert.AreEqual("purple,red,orange", SkyDecorColours.Format("PURPLE, red:1, Orange"));
            Assert.AreEqual("grey", SkyDecorColours.Format("gray"), "an alias is normalised to the canonical name");
            Assert.AreEqual("green:0", SkyDecorColours.Format("green:0"), "a switched-off entry keeps its zero");
            Assert.AreEqual(string.Empty, SkyDecorColours.Format(""));
            Assert.AreEqual(string.Empty, SkyDecorColours.Format(null));
        }

        /// <summary>A wcid that is not one of the eight is echoed as itself rather than dropped.</summary>
        [TestMethod]
        public void Colours_Format_FallsBackToTheRawWcid()
        {
            Assert.AreEqual("purple,1234567", SkyDecorColours.Format("1002642,1234567"));
        }

        /// <summary>
        /// The property /sky-decor sql leans on: whatever Format prints parses back to the entry list it
        /// was printed from. Without it, pasting the generated UPDATE could quietly change the sky.
        /// </summary>
        [TestMethod]
        public void Colours_Format_RoundTripsThroughParsePalette()
        {
            foreach (var input in new[]
            {
                "purple,red,orange",
                "1002642,1002647:2,1002646",
                "PURPLE:3,gray,1234567:5",
                "green:0,blue",
                "white",
            })
            {
                var first = SkyDecorLayout.ParsePalette(input);
                var rendered = SkyDecorColours.Format(input);
                var second = SkyDecorLayout.ParsePalette(rendered);

                CollectionAssert.AreEqual(first.Select(e => e.WeenieClassId).ToArray(), second.Select(e => e.WeenieClassId).ToArray(), input);
                CollectionAssert.AreEqual(first.Select(e => e.Weight).ToArray(), second.Select(e => e.Weight).ToArray(), input);

                Assert.AreEqual(rendered, SkyDecorColours.Format(rendered), $"{input}: rendering is not idempotent");
            }
        }

        [TestMethod]
        public void Overrides_SetPaletteByName_StoresTheCanonicalForm()
        {
            var db = FreshOverrideFixture();

            Assert.IsTrue(SkyDecorOverrides.TryApply(db, "palette", "PURPLE, red , Orange", out var updated, out var error), error);

            Assert.AreEqual("purple,red,orange", updated.Palette, "the stored column is normalised, not echoed");
            Assert.AreEqual("purple,red,orange", SkyDecorOverrides.Read(updated, "palette"));

            CollectionAssert.AreEqual(new uint[] { 1002642, 1002647, 1002646 },
                SkyDecorLayout.ParsePalette(updated.Palette).Select(e => e.WeenieClassId).ToArray());

            // A palette typed as wcids is normalised to names too, so the column reads the same either way.
            Assert.IsTrue(SkyDecorOverrides.TryApply(db, "palette", "1002642,1002647,1002646", out var byWcid, out error), error);
            Assert.AreEqual("purple,red,orange", byWcid.Palette);
        }

        [TestMethod]
        public void Overrides_SetPalette_UnknownName_IsRejectedWithTheNameList()
        {
            var db = FreshOverrideFixture();

            Assert.IsFalse(SkyDecorOverrides.TryApply(db, "palette", "purple,chartreuse", out var bad, out var error));

            Assert.IsNull(bad, "a partly-valid palette must be refused whole, not silently trimmed");
            Assert.IsTrue(error.Contains("chartreuse"), error);
            Assert.IsTrue(error.Contains("orange"), "the error must list the valid names: " + error);
        }

        /// <summary>
        /// The set path is the STRICT one - an unknown name is refused rather than skipped - but white is
        /// not unknown, it is discouraged, so it goes through with a note attached.
        /// </summary>
        [TestMethod]
        public void Overrides_SetPalette_White_IsAcceptedWithAWarning()
        {
            var db = FreshOverrideFixture();

            Assert.IsTrue(SkyDecorOverrides.TryApply(db, "palette", "purple,white", out var updated, out var error, out var warnings), error);

            Assert.AreEqual("purple,white", updated.Palette);
            Assert.AreEqual(1, warnings.Count);
            Assert.IsTrue(warnings[0].Contains("pixelated"), warnings[0]);

            // A refused edit reports no advisories at all - they would be about a change that did not happen.
            Assert.IsFalse(SkyDecorOverrides.TryApply(db, "palette", "white,chartreuse", out var bad, out error, out var noWarnings));
            Assert.IsNull(bad);
            Assert.AreEqual(0, noWarnings.Count);
        }

        /// <summary>
        /// The trap this advisory exists for: under 'mast' a disc stands partOffset * scale above an
        /// origin pinned near the ground, so raising the scale also raises the whole sky and the height
        /// columns stop deciding anything. That is invisible from the reply `set` prints, and it cost a
        /// live tuning round on 2026-08-20 before anyone worked out why the clouds kept climbing.
        /// </summary>
        [TestMethod]
        public void Overrides_SetScale_UnderMast_WarnsThatHeightMovesToo()
        {
            var db = FreshOverrideFixture();

            Assert.IsTrue(SkyDecorOverrides.TryApply(db, "scale_max", "300", out var updated, out var error, out var warnings), error);

            Assert.AreEqual(300.0f, updated.ScaleMax);
            Assert.AreEqual(1, warnings.Count);
            Assert.IsTrue(warnings[0].Contains("HEIGHT"), warnings[0]);

            // The band it quotes is the one the NEW numbers produce: 0.455 * 300 + 1 = 137.5 m.
            Assert.IsTrue(warnings[0].Contains("137.5"), warnings[0]);
            Assert.IsTrue(warnings[0].Contains("inverted"), "it must name the way out: " + warnings[0]);

            // Editing the height columns under the same rig is just as misleading, so it warns there too.
            Assert.IsTrue(SkyDecorOverrides.TryApply(db, "height_max", "60", out _, out error, out var heightWarnings), error);
            Assert.AreEqual(1, heightWarnings.Count);

            // An unrelated column says nothing - the advisory has to stay rare enough to read.
            Assert.IsTrue(SkyDecorOverrides.TryApply(db, "density", "3", out _, out error, out var densityWarnings), error);
            Assert.AreEqual(0, densityWarnings.Count);
        }

        [TestMethod]
        public void Overrides_SetScale_UnderInvertedOrWhenHeightsClearTheDisc_IsSilent()
        {
            var db = FreshOverrideFixture();

            // The shipped pose: size and height are independent, so there is nothing to warn about.
            Assert.IsTrue(SkyDecorOverrides.TryApply(db, "rig", "inverted", out var inverted, out var error, out var rigWarnings), error);
            Assert.AreEqual(0, rigWarnings.Count);

            Assert.IsTrue(SkyDecorOverrides.TryApply(inverted, "scale_max", "300", out _, out error, out var warnings), error);
            Assert.AreEqual(0, warnings.Count);

            // Mast, but every rolled height already clears 0.455 * 240 + 1 = 110.2 m, so the height
            // columns really do decide and the coupling never binds.
            var high = MakeRegion(r =>
            {
                r.Name = "mast_high";
                r.Rig = SkyDecorLayout.RigMast;
                r.ScaleMin = 96.0f;
                r.ScaleMax = 240.0f;
                r.HeightMin = 150.0f;
                r.HeightMax = 200.0f;
            });

            Assert.IsTrue(SkyDecorOverrides.TryApply(high, "height_min", "160", out _, out error, out var quiet), error);
            Assert.AreEqual(0, quiet.Count);
        }

        [TestMethod]
        public void Overrides_Sql_RendersThePaletteByName()
        {
            SkyDecorOverrides.ClearAll();

            var region = MakeRegion(r => { r.Name = "sql_palette"; r.Palette = "1002642,1002647:2,1002646"; });

            Assert.IsTrue(SkyDecorOverrides.BuildUpdateSql(region).Contains("palette='purple,red:2,orange'"),
                SkyDecorOverrides.BuildUpdateSql(region));
        }

        // =======================================================================================
        // The shipped Marae row
        // =======================================================================================

        /// <summary>
        /// The region as Content/realms/marae_lassel_sky_decor.sql actually ships it. A golden, so a
        /// tuning change announces itself here instead of being discovered by flying over the island.
        ///
        /// The shape changed completely on 2026-08-20 after the live blink investigation: the client
        /// drops a very large disc in and out of the scene on a small change of facing, at every scale
        /// tested, and nothing reachable from the server influences it (see the unit header). The answer
        /// is not to avoid it but to make one dropout cost nothing - many enormous, almost perfectly
        /// transparent discs, so losing one changes the sky by well under a percent. Hence density 15,
        /// scale 1000-3000 and translucency 0.99-0.995, where the old row had 2.5, 96-240 and opaque.
        /// Pairing is off, so density counts objects directly rather than pairs.
        ///
        /// The rectangle is 37 x 33 = 1221 landblocks, and the counts below are what would spawn if
        /// EVERY one of them were loaded at once, which never happens - a landblock that no player
        /// visits never plans. It is the ceiling, not a live figure. The live figure is the per-block
        /// number, also asserted, because that one is what a player's 3x3 actually pays for.
        /// </summary>
        [TestMethod]
        public void MaraeIsland_AsShipped_HasAKnownSurvivorTotal()
        {
            var region = MakeRegion(r =>
            {
                r.Name = "marae_lassel_probe";
                r.LbXMin = 0x0B;
                r.LbXMax = 0x2F;
                r.LbYMin = 0xAB;
                r.LbYMax = 0xCB;
                r.Seed = 20260820;
                r.Version = 4;
                r.Density = 15.0f;
                r.Palette = "purple";
                r.ScaleMin = 1000.0f;
                r.ScaleMax = 3000.0f;
                r.HeightMin = 50.0f;
                r.HeightMax = 150.0f;
                r.SpinMin = 0.0f;
                r.SpinMax = 0.0f;
                r.PairChance = 0.0f;
                r.MinSeparation = 0.0f;
                r.TranslucencyMin = 0.99f;
                r.TranslucencyMax = 0.995f;
                r.Rig = SkyDecorLayout.RigInverted;
            });

            var blocks = BlocksOf(region);

            Assert.AreEqual(1221, blocks.Count, "37 x 33 landblocks");

            // Separation off, so no cross-block sweep at all. That matters at this size: the cull is
            // capped at MaxNeighbourBlockRadius = 3 (576 m), and a scale-3000 disc's 2175 m radius would
            // have blown past it and produced disagreeing verdicts on either side of a boundary.
            Assert.AreEqual(0, SkyDecorLayout.NeighbourBlockRadius(region));

            var candidates = 0;
            var clouds = 0;
            var objects = 0;

            foreach (var block in blocks)
            {
                var plan = SkyDecorLayout.PlanBlock(region, block, FlatGround, null);

                candidates += plan.Candidates;
                clouds += plan.Clouds.Count;
                objects += plan.Clouds.Sum(c => c.Partner != null ? 2 : 1);
            }

            Assert.AreEqual(18315, candidates, "candidate total over the whole rectangle");
            Assert.AreEqual(18315, clouds, "every candidate survives - separation off, over_water on");
            Assert.AreEqual(18315, objects, "pairing is off, so one object per cloud");

            // 15 objects per landblock, so 135 in a player's 3x3 window. This is the number to watch if
            // density is ever raised again - the totals above are a ceiling nobody pays, this one is not.
            Assert.AreEqual(15, objects / blocks.Count, "objects per landblock");
        }

        /// <summary>
        /// The shipped region is a SINGLE colour, which exercises the palette's one-entry path: with one
        /// entry the pick has no alternative to fall back on, and getting that wrong fails silently as a
        /// cloudless sky rather than as an exception. Pairing is off in the shipped row, so this also
        /// pins that every planned cloud is a lone disc.
        /// </summary>
        [TestMethod]
        public void MaraeIsland_AsShipped_IsEntirelyOneColour()
        {
            var region = MakeRegion(r =>
            {
                // The island rectangle, not MakeRegion's 5x5 default - otherwise "as shipped" would be
                // measuring a fixture rather than the row this test claims to guard.
                r.LbXMin = 0x0B;
                r.LbXMax = 0x2F;
                r.LbYMin = 0xAB;
                r.LbYMax = 0xCB;
                r.Version = 4;
                r.Density = 15.0f;
                r.Palette = "purple";
                r.ScaleMin = 1000.0f;
                r.ScaleMax = 3000.0f;
                r.HeightMin = 50.0f;
                r.HeightMax = 150.0f;
                r.PairChance = 0.0f;
                r.MinSeparation = 0.0f;
                r.Rig = SkyDecorLayout.RigInverted;
            });

            var discs = 0;

            foreach (var block in BlocksOf(region))
            {
                foreach (var cloud in SkyDecorLayout.Plan(region, block, FlatGround, null))
                {
                    Assert.AreEqual(1002642u, cloud.WeenieClassId, "a one-entry palette can only ever produce that entry");
                    Assert.IsNull(cloud.Partner, "pair_chance 0 must produce no partners at all");

                    discs++;
                }
            }

            Assert.AreEqual(18315, discs, "every disc accounted for");
        }

        /// <summary>
        /// STANDARD SKY PLACEMENT, and the reason the shipped row went back to 'inverted' on 2026-08-20:
        /// the disc lands at exactly ground + rolled height whatever the scale is, so scale_min/scale_max
        /// resize the clouds without moving them. Doubling the whole scale range must leave every disc
        /// height untouched - and under 'mast' the same doubling must MOVE them, which is the coupling
        /// the owner hit while tuning ("setting the scale also increases the elevation").
        ///
        /// min_separation is 0 on both sides so the two runs cull identically and the comparison is
        /// cloud-for-cloud; at the shipped 0.5 a doubled radius would drop a different set.
        /// </summary>
        [TestMethod]
        public void Shipped_ScaleDoesNotMoveTheDiscHeight()
        {
            Func<float, float, string, SkyDecorRegion> shipped = (scaleMin, scaleMax, rig) => MakeRegion(r =>
            {
                r.Version = 4;
                r.Density = 2.5f;
                r.Palette = "purple,red,orange";
                r.ScaleMin = scaleMin;
                r.ScaleMax = scaleMax;
                r.HeightMin = 45.0f;
                r.HeightMax = 110.0f;
                r.MinSeparation = 0.0f;
                r.Rig = rig;
            });

            var compared = 0;
            var movedUnderMast = 0;

            foreach (var block in BlocksOf(shipped(96.0f, 240.0f, SkyDecorLayout.RigInverted)))
            {
                var normal = SkyDecorLayout.Plan(shipped(96.0f, 240.0f, SkyDecorLayout.RigInverted), block, FlatGround, null);
                var bigger = SkyDecorLayout.Plan(shipped(192.0f, 480.0f, SkyDecorLayout.RigInverted), block, FlatGround, null);
                var mast = SkyDecorLayout.Plan(shipped(192.0f, 480.0f, SkyDecorLayout.RigMast), block, FlatGround, null);

                Assert.AreEqual(normal.Count, bigger.Count, $"0x{block:X4}: scale must not change how many clouds there are");

                for (var i = 0; i < normal.Count; i++)
                {
                    Assert.AreEqual(normal[i].Height, bigger[i].Height, 0.001f,
                        $"0x{block:X4} cloud {i}: doubling the scale must leave the disc where it was");

                    Assert.IsTrue(bigger[i].Scale > normal[i].Scale, "the disc must actually have got bigger");

                    Assert.IsTrue(normal[i].Height >= 44.999f && normal[i].Height <= 110.001f,
                        $"the disc sits in the height band the row asks for, not one derived from scale: {normal[i].Height}");

                    if (Math.Abs(mast[i].Height - normal[i].Height) > 0.001f)
                        movedUnderMast++;

                    compared++;
                }
            }

            Assert.IsTrue(compared > 0, "the fixture produced no clouds to compare");

            Assert.IsTrue(movedUnderMast > 0,
                "under 'mast' the same doubling DOES move the sky - that coupling is what this row was reverted to avoid");
        }

        // =======================================================================================
        // Rig: 'mast'
        // =======================================================================================

        private static SkyDecorRegion MakeMastRegion(Action<SkyDecorRegion> tweak = null)
        {
            return MakeRegion(r =>
            {
                r.Rig = SkyDecorLayout.RigMast;
                r.ScaleMin = 96.0f;
                r.ScaleMax = 240.0f;

                tweak?.Invoke(r);
            });
        }

        [TestMethod]
        public void IsMast_RecognisesOnlyMastAndIsCaseInsensitive()
        {
            Assert.IsTrue(SkyDecorLayout.IsMast("mast"));
            Assert.IsTrue(SkyDecorLayout.IsMast("MAST"));
            Assert.IsFalse(SkyDecorLayout.IsMast(SkyDecorLayout.RigInverted));
            Assert.IsFalse(SkyDecorLayout.IsMast(null), "an unset rig column must read as the original inverted rig");
            Assert.IsFalse(SkyDecorLayout.IsMast("anything else"));
        }

        /// <summary>
        /// The point of the mast rig: the ORIGIN is at the player's feet, so the disc overhead visibly
        /// turns instead of reading as a static ceiling. At any scale this region ships, the rolled height
        /// is smaller than the disc's own offset, so the clearance floor is what decides the origin.
        /// </summary>
        [TestMethod]
        public void Mast_OriginZ_SitsTheClearanceAboveTheGround()
        {
            var region = MakeMastRegion();

            var clouds = SkyDecorLayout.Plan(region, MaraeBluespire, FlatGround, null);

            Assert.IsTrue(clouds.Count > 0);

            foreach (var cloud in clouds)
                Assert.AreEqual(12.0f + SkyDecorLayout.MastOriginClearance, cloud.OriginZ, 1e-4f);
        }

        /// <summary>
        /// The origin is never allowed under the terrain. The spinning_scenery doctrine records
        /// below-terrain origins being snapped up to the surface, which drags the disc with them and
        /// breaks the geometry with no error anywhere.
        /// </summary>
        [TestMethod]
        public void Mast_OriginZ_IsNeverBelowTheGround()
        {
            var region = MakeMastRegion(r => { r.Density = 5.0f; r.HeightMin = -50.0f; r.HeightMax = -10.0f; });

            foreach (var block in BlocksOf(region))
            {
                foreach (var cloud in SkyDecorLayout.Plan(region, block, FlatGround, null).SelectMany(c => c.WithPartner()))
                    Assert.IsTrue(cloud.OriginZ >= 12.0f + SkyDecorLayout.MastOriginClearance - 1e-4f, $"origin {cloud.OriginZ} is below the ground");
            }
        }

        /// <summary>The disc stands part_offset x scale ABOVE the origin, which is the whole difference from inverted.</summary>
        [TestMethod]
        public void Mast_DiscCentre_IsPartOffsetAboveTheOrigin()
        {
            var region = MakeMastRegion();

            foreach (var cloud in SkyDecorLayout.Plan(region, MaraeBluespire, FlatGround, null))
            {
                var theta = cloud.TiltDeg * Math.PI / 180.0;

                var expected = cloud.OriginZ + (float)(region.PartOffset * cloud.Scale * Math.Cos(theta));

                Assert.AreEqual(expected, cloud.GroundZ + cloud.Height, 1e-3f);
                Assert.IsTrue(cloud.Height > 0.0f, "a mast disc is above the ground, not below it");
            }
        }

        /// <summary>
        /// Under mast, height_min/height_max are effectively inert at the shipped scales - scale sets the
        /// height. The row header says so, and this is what makes that true.
        /// </summary>
        [TestMethod]
        public void Mast_AtShippedScales_HeightIsSetByScaleNotByTheHeightColumns()
        {
            var low = MakeMastRegion(r => { r.HeightMin = 25.0f; r.HeightMax = 25.0f; });
            var high = MakeMastRegion(r => { r.HeightMin = 50.0f; r.HeightMax = 50.0f; });

            var a = SkyDecorLayout.Plan(low, MaraeBluespire, FlatGround, null);
            var b = SkyDecorLayout.Plan(high, MaraeBluespire, FlatGround, null);

            Assert.AreEqual(a.Count, b.Count);

            for (var i = 0; i < a.Count; i++)
                Assert.AreEqual(a[i].OriginZ, b[i].OriginZ, 1e-4f, "the height column moved a mast origin it should not reach");
        }

        /// <summary>
        /// ... but it is not DEAD. Drop the scale far enough that the rolled height clears the disc offset
        /// plus the clearance and the column takes over again, which is what stops this being a silent
        /// trap for a future small-scale mast region.
        /// </summary>
        [TestMethod]
        public void Mast_AtSmallScales_TheHeightColumnTakesOverAgain()
        {
            var region = MakeMastRegion(r =>
            {
                r.ScaleMin = 10.0f;
                r.ScaleMax = 10.0f;
                r.HeightMin = 30.0f;
                r.HeightMax = 30.0f;
                r.TiltMaxDeg = 0.0f;
            });

            foreach (var cloud in SkyDecorLayout.Plan(region, MaraeBluespire, FlatGround, null))
            {
                Assert.AreEqual(30.0f, cloud.Height, 1e-3f, "the rolled height must be honoured when it clears the disc offset");
                Assert.AreEqual(12.0f + 30.0f - 0.455f * 10.0f, cloud.OriginZ, 1e-3f);
            }
        }

        [TestMethod]
        public void DiscNormal_Mast_PointsUp()
        {
            var n = SkyDecorLayout.DiscNormal(137.0f, 0.0f, true);

            Assert.AreEqual(0.0f, n.X, 1e-5f);
            Assert.AreEqual(0.0f, n.Y, 1e-5f);
            Assert.AreEqual(1.0f, n.Z, 1e-5f);

            var tilted = SkyDecorLayout.DiscNormal(137.0f, 5.0f, true);

            Assert.AreEqual(1.0f, tilted.Length(), 1e-5f);
            Assert.IsTrue(tilted.Z > 0.99f, "a 5 degree mast disc still points essentially straight up");
        }

        /// <summary>
        /// mast is the mirror of inverted about the horizontal plane: theta = tilt instead of 180 - tilt.
        /// At zero tilt that leaves a pure yaw, which is the flat plate the region wants.
        /// </summary>
        [TestMethod]
        public void Orientation_Mast_ZeroTilt_IsAPureYaw()
        {
            SkyDecorLayout.Orientation(90.0f, 0.0f, true, out var w, out var x, out var y, out var z);

            Assert.AreEqual((float)Math.Cos(Math.PI / 4.0), w, 1e-5f);
            Assert.AreEqual(0.0f, x, 1e-5f);
            Assert.AreEqual(0.0f, y, 1e-5f);
            Assert.AreEqual((float)Math.Sin(Math.PI / 4.0), z, 1e-5f);
        }

        [TestMethod]
        public void Orientation_Mast_IsAlwaysAUnitQuaternion()
        {
            for (var yaw = 0; yaw < 360; yaw += 17)
            {
                for (var tilt = 0; tilt <= 45; tilt += 5)
                {
                    SkyDecorLayout.Orientation(yaw, tilt, true, out var w, out var x, out var y, out var z);

                    Assert.AreEqual(1.0f, (float)Math.Sqrt(w * w + x * x + y * y + z * z), 1e-5f, $"yaw {yaw} tilt {tilt}");
                }
            }
        }

        /// <summary>
        /// Under mast the partner still hangs BELOW the primary's disc, but its origin lands ABOVE the
        /// primary's - which is what keeps it off the ground that the mast rig deliberately parks the
        /// primary origin one metre over. Getting the sign of pair_gap wrong here would bury it.
        /// </summary>
        [TestMethod]
        public void Mast_Partner_HangsBelowTheDiscAndAboveThePrimaryOrigin()
        {
            var region = MakeMastRegion();

            var clouds = SkyDecorLayout.Plan(region, MaraeBluespire, FlatGround, null);

            Assert.IsTrue(clouds.Count > 0);

            foreach (var cloud in clouds)
            {
                var partner = cloud.Partner;

                Assert.IsNotNull(partner);

                var n = SkyDecorLayout.DiscNormal(cloud.YawDeg, cloud.TiltDeg, true);

                var primaryDisc = cloud.OriginZ + region.PartOffset * cloud.Scale * n.Z;
                var partnerDisc = partner.OriginZ + region.PartOffset * partner.Scale * n.Z;

                Assert.AreEqual(primaryDisc - region.PairGap * n.Z, partnerDisc, 1e-3f, "the partner disc must sit pair_gap below the primary disc along the normal");

                Assert.IsTrue(partner.OriginZ > cloud.OriginZ, "a mast partner's origin belongs above the primary's, not underground");
                Assert.IsTrue(partner.OriginZ > partner.GroundZ, "a partner origin must never be below the terrain");
            }
        }

        /// <summary>
        /// The inverted rig must be untouched by all of this. rig = 'inverted' and an unset rig column
        /// have to produce the layout this suite already asserts, bit for bit - the fingerprint includes
        /// origin, orientation and both discs of every pair.
        /// </summary>
        [TestMethod]
        public void Rig_InvertedAndUnset_ProduceTheIdenticalPreExistingLayout()
        {
            var unset = MakeRegion();
            var explicitInverted = MakeRegion(r => r.Rig = SkyDecorLayout.RigInverted);
            var mast = MakeRegion(r => r.Rig = SkyDecorLayout.RigMast);

            var baseline = Fingerprint(SkyDecorLayout.Plan(unset, MaraeBluespire, FlatGround, null));

            Assert.AreEqual(baseline, Fingerprint(SkyDecorLayout.Plan(explicitInverted, MaraeBluespire, FlatGround, null)));

            Assert.AreNotEqual(baseline, Fingerprint(SkyDecorLayout.Plan(mast, MaraeBluespire, FlatGround, null)),
                "mast must actually change the geometry, or the previous assertion proves nothing");
        }

        /// <summary>
        /// The rig changes only the GEOMETRY, never the dice: same cells, same scales, same colours, same
        /// pairing decisions. That is what lets the owner flip rig on a live region and see it re-pose
        /// rather than re-shuffle.
        /// </summary>
        [TestMethod]
        public void Rig_DoesNotDisturbTheRandomStream()
        {
            var inverted = SkyDecorLayout.Plan(MakeRegion(), MaraeBluespire, FlatGround, null);
            var mast = SkyDecorLayout.Plan(MakeRegion(r => r.Rig = SkyDecorLayout.RigMast), MaraeBluespire, FlatGround, null);

            Assert.AreEqual(inverted.Count, mast.Count);

            for (var i = 0; i < inverted.Count; i++)
            {
                Assert.AreEqual(inverted[i].WeenieClassId, mast[i].WeenieClassId);
                Assert.AreEqual(inverted[i].CellX, mast[i].CellX);
                Assert.AreEqual(inverted[i].CellY, mast[i].CellY);
                Assert.AreEqual(inverted[i].X, mast[i].X, 1e-6f);
                Assert.AreEqual(inverted[i].Y, mast[i].Y, 1e-6f);
                Assert.AreEqual(inverted[i].Scale, mast[i].Scale, 1e-6f);
                Assert.AreEqual(inverted[i].Spin, mast[i].Spin, 1e-6f);
                Assert.AreEqual(inverted[i].TiltDeg, mast[i].TiltDeg, 1e-6f);
                Assert.AreEqual(inverted[i].YawDeg, mast[i].YawDeg, 1e-6f);
                Assert.AreEqual(inverted[i].Partner?.WeenieClassId, mast[i].Partner?.WeenieClassId);
            }
        }
    }
}
