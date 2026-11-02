using System;
using System.Linq;
using System.Numerics;

using ACE.Server.HazardMechanics;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.HazardMechanics
{
    /// <summary>
    /// The wisp mechanic's decisions that are not geometry (WispRules): settings, every Validate refusal, fire order,
    /// timeline, PlaceWisp over a formation. Pure: HazardArgs only, never PropertyManager.
    /// </summary>
    [TestClass]
    public class WispRulesTests
    {
        private static HazardRegion Pinnacle() => HazardRegionPresets.Pinnacle(0);

        private static HazardRegion AdHocRect(float w, float h)
        {
            var r = HazardRegion.AdHoc(0x0123, 0, false, new Vector3(100f, -60f, -47.75f), HazardShapeSpec.Rect(w, h), 0f, out var error);
            Assert.IsNull(error);
            return r;
        }

        private static string V(HazardRegion region, string args, string pattern = "scatter")
            => WispRules.Validate(region, HazardArgs.Parse(args), pattern);

        private static WispSettings S(string args = "") => WispSettings.From(HazardArgs.Parse(args));

        /// <summary>The ceiling refusal (pure): on the Pinnacle a scale-3 wisp would reach the ceiling, scale 1 fits.</summary>
        [TestMethod]
        public void PinnacleWisps_Scale3_IsRefusedUnderTheCeiling_Scale1_IsAccepted()
        {
            var region = Pinnacle();
            var defaults = HazardArgs.Parse(HazardDefaults.Wisps);

            var refused = WispRules.Validate(region, HazardArgs.Merge(HazardArgs.Parse("scale=3"), defaults), "row");
            Assert.IsNotNull(refused, "scale=3: 5.49 m of wisp from -47.25 reaches -41.76, above the ceiling -42.05");
            StringAssert.Contains(refused, "largest scale that fits is 2.78");
            Assert.IsFalse(refused.Contains('\n'), "one line");

            Assert.IsNull(WispRules.Validate(region, HazardArgs.Merge(HazardArgs.Parse("scale=1"), defaults), "row"));
            Assert.IsNull(WispRules.Validate(region, HazardArgs.Merge(HazardArgs.Parse("scale=2.78"), defaults), "row"), "the named maximum is accepted");
            Assert.AreEqual(2.787f, WispRules.MaxScaleUnderCeiling(region).Value, 0.001f, "(-42.05 - 0.1 + 47.25) / 1.83");

            // no ceiling, no limit
            var open = HazardRegion.Rect("open", 0x0069, 0, false, -4.6f, 34.6f, -34.6f, -5.4f, HazardGroundMode.Plane, -47.75f, null, 0.5f, null, 6.5f, 5.7f);
            Assert.IsNull(WispRules.MaxScaleUnderCeiling(open));
            Assert.IsNull(WispRules.Validate(open, HazardArgs.Merge(HazardArgs.Parse("scale=3"), defaults), "row"));
        }

        // ---- settings ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void Settings_CodeDefaultsAreTheShippedSettingsString()
        {
            var shipped = WispSettings.From(HazardArgs.Parse(HazardDefaults.Wisps));
            var bare = WispSettings.From(HazardArgs.Empty);

            Assert.AreEqual(8, shipped.Count);
            Assert.AreEqual(1.0f, shipped.Scale);
            Assert.AreEqual(2.5, shipped.Move);
            Assert.AreEqual(1.5, shipped.Hold);
            Assert.AreEqual(1.5, shipped.Burst);
            Assert.AreEqual(1.0, shipped.Rest);
            Assert.AreEqual(2.0f, shipped.PillarRadius);
            Assert.AreEqual(8, shipped.Cycles);

            Assert.AreEqual(shipped.Count, bare.Count);
            Assert.AreEqual(shipped.Scale, bare.Scale);
            Assert.AreEqual(shipped.Move, bare.Move);
            Assert.AreEqual(shipped.Hold, bare.Hold);
            Assert.AreEqual(shipped.Burst, bare.Burst);
            Assert.AreEqual(shipped.Rest, bare.Rest);
            Assert.AreEqual(shipped.PillarRadius, bare.PillarRadius);
            Assert.AreEqual(shipped.Cycles, bare.Cycles);

            Assert.AreEqual(WispFire.Unison, bare.Fire);
            Assert.AreEqual("random", bare.Dir);
            Assert.AreEqual(HazardDefaults.EmberWispWcid, bare.WispWcid);
            Assert.AreEqual(HazardDefaults.PillarScript, bare.Script);
        }

        [TestMethod]
        public void Settings_AreClamped_AndCallerArgsBeatTheSettingsString()
        {
            var s = S("n=999 hold=0.01 pillar_radius=99 move=0 cycles=0 burst=-4 scale=50 steps=999");

            Assert.AreEqual(32, s.Count);
            Assert.AreEqual(0.2, s.Hold, 1e-9);
            Assert.AreEqual(15f, s.PillarRadius);
            Assert.AreEqual(0.5, s.Move, 1e-9);
            Assert.AreEqual(1, s.Cycles);
            Assert.AreEqual(0.2, s.Burst, 1e-9);
            Assert.AreEqual(4f, s.Scale);
            Assert.AreEqual(40, s.Steps);

            var merged = HazardArgs.Merge(HazardArgs.Parse("hold=3 fire=sequence"), HazardArgs.Parse(HazardDefaults.Wisps));
            Assert.AreEqual(3.0, WispSettings.From(merged).Hold, 1e-9);
            Assert.AreEqual(WispFire.Sequence, WispSettings.From(merged).Fire);
            Assert.AreEqual(8, WispSettings.From(merged).Count, "the settings string still supplies the rest");
        }

        [TestMethod]
        public void EveryPillarOfEveryPattern_UsesPillarRadiusAndTheOneHitPerHostWindow()
        {
            var s = S("pillar_radius=3.5 burst=2.25");

            Assert.AreEqual(3.5f, s.HitRadius);
            Assert.IsTrue(double.IsPositiveInfinity(s.HitWindow), "one hit per pillar host, like the balls");
            Assert.AreEqual(2f * 3.5f + 2f * 0.5f + 0.5f, s.MinSpacing, 1e-6, "MinSpacing = 2 x pillar_radius + 2 x player_radius + 0.5");
            Assert.AreEqual(1.5f, S().MinGap, 1e-6);
        }

        // ---- Validate: every refusal, each with a positive control --------------------------------------------------

        [TestMethod]
        public void Validate_AcceptsTheShippedDefaults_ForEveryPattern_OnThePinnacle()
        {
            foreach (var pattern in WispRules.Patterns)
                Assert.IsNull(V(Pinnacle(), HazardDefaults.Wisps, pattern), pattern);
        }

        [TestMethod]
        public void Validate_RefusesARegionTooSmallForOnePillar()
        {
            var tiny = AdHocRect(8f, 8f); // inscribed 4, usable 2; one pillar needs 2 + 0.5 + 0.75 = 3.25

            var refusal = V(tiny, "");
            Assert.IsNotNull(refusal);
            StringAssert.Contains(refusal, "region too small");
            Assert.IsFalse(refusal.Contains('\n'));

            Assert.IsNull(V(tiny, "edge_inset=0"), "positive control: with no inset the same region is fine");
            Assert.IsNotNull(V(AdHocRect(20f, 20f), "pillar_radius=9"), "a bigger pillar makes a bigger region too small");
            Assert.IsNull(V(AdHocRect(20f, 20f), "pillar_radius=5 hold=3"));
        }

        [TestMethod]
        public void Validate_RefusesAHoldUnderTheEscapeTime_AtExactlyTheBound()
        {
            // floor = 0.5 + (pillar_radius + player_radius) / escape_speed = 0.5 + 2.5 / 2.5 = 1.5
            StringAssert.Contains(V(Pinnacle(), "escape_speed=2.5 hold=1.49"), "hold");
            Assert.IsNull(V(Pinnacle(), "escape_speed=2.5 hold=1.5"), "exactly the bound is allowed");
            Assert.IsNull(V(Pinnacle(), "escape_speed=2.5 hold=1.51"));

            // the default 3 m/s: 0.5 + 2.5 / 3 = 1.3333
            StringAssert.Contains(V(Pinnacle(), "hold=1.33"), "hold");
            Assert.IsNull(V(Pinnacle(), "hold=1.34"));
            Assert.AreEqual(0.5 + 2.5 / 3.0, S().EscapeHoldFloor, 1e-9);

            // a slower escape speed needs a longer telegraph, a faster one a shorter
            StringAssert.Contains(V(Pinnacle(), "escape_speed=1 hold=2.9"), "hold");
            Assert.IsNull(V(Pinnacle(), "escape_speed=1 hold=3.0"));
            Assert.IsNull(V(Pinnacle(), "escape_speed=12 hold=0.71"));
            StringAssert.Contains(V(Pinnacle(), "escape_speed=12 hold=0.7"), "hold");

            // a bigger pillar needs longer too
            Assert.AreEqual(0.5 + 3.5 / 3.0, S("pillar_radius=3").EscapeHoldFloor, 1e-9);
            StringAssert.Contains(V(Pinnacle(), "pillar_radius=3 hold=1.6"), "hold");
            Assert.IsNull(V(Pinnacle(), "pillar_radius=3 hold=1.7"));

            // escape_speed is clamped 1..12
            Assert.AreEqual(1.0, S("escape_speed=0.01").EscapeSpeed, 1e-9);
            Assert.AreEqual(12.0, S("escape_speed=99").EscapeSpeed, 1e-9);
            Assert.AreEqual(3.0, S().EscapeSpeed, 1e-9);
        }

        [TestMethod]
        public void Validate_RefusesAnExclusionBiggerThanAPillarsReach_AndAnAllExcludedRegion()
        {
            HazardRegion Rect40(params HazardExclusion[] exclusions) => HazardRegion.Rect("r40", 0x0123, 0, false, 0f, 40f, 0f, 40f,
                HazardGroundMode.Plane, 0f, null, 0f, null, 2f, 6f, exclusions);

            // reach = pillar_radius 2 + player_radius 0.5 = 2.5
            var big = WispRules.Validate(Rect40(HazardExclusion.Circle(20f, 20f, 9f)), HazardArgs.Empty, "scatter");
            StringAssert.Contains(big, "exclusion");
            StringAssert.Contains(big, "forever");

            Assert.IsNull(WispRules.Validate(Rect40(HazardExclusion.Circle(20f, 20f, 2.5f)), HazardArgs.Empty, "scatter"), "exactly the reach is allowed");
            StringAssert.Contains(WispRules.Validate(Rect40(HazardExclusion.Circle(20f, 20f, 2.6f)), HazardArgs.Empty, "scatter"), "exclusion");

            // a rect exclusion counts by its inscribed radius: half its shorter side
            Assert.IsNull(WispRules.Validate(Rect40(HazardExclusion.Rect(10f, 20f, 10f, 14f)), HazardArgs.Empty, "scatter"), "10 x 4 has inscribed radius 2");
            StringAssert.Contains(WispRules.Validate(Rect40(HazardExclusion.Rect(10f, 16f, 10f, 16f)), HazardArgs.Empty, "scatter"), "exclusion");

            // a raised pillar_radius makes the same exclusion small enough
            Assert.IsNull(WispRules.Validate(Rect40(HazardExclusion.Circle(20f, 20f, 4f)), HazardArgs.Parse("pillar_radius=4 hold=3"), "scatter"));

            // an all-excluded region: 2 x 2 tiles, each small, together covering everything
            var tiles = new System.Collections.Generic.List<HazardExclusion>();

            for (var x = 0; x < 20; x += 2)
                for (var y = 0; y < 20; y += 2)
                    tiles.Add(HazardExclusion.Rect(x, x + 2, y, y + 2));

            var tiled = HazardRegion.Rect("tiled", 0x0123, 0, false, 0f, 20f, 0f, 20f, HazardGroundMode.Plane, 0f, null, 0f, null, 2f, 6f, tiles);
            var refusal = WispRules.Validate(tiled, HazardArgs.Empty, "scatter");

            StringAssert.Contains(refusal, "nowhere to fire");
            Assert.IsFalse(WispRules.HasPlaceableSpot(tiled, 2f));
            Assert.IsTrue(WispRules.HasPlaceableSpot(Rect40(), 2f));
        }

        [TestMethod]
        public void Validate_RefusesNonNumericValues_ForEveryNumericArg_AndNBelowOne()
        {
            foreach (var key in new[] { "scale", "move", "hold", "burst", "rest", "pillar_radius", "seq_gap", "sweep_move", "ring_radius", "cx", "cy", "scatter_cover", "wisp_clear", "escape_speed", "player_radius", "edge_inset", "pct" })
            {
                var refusal = V(Pinnacle(), key + "=abc");

                StringAssert.Contains(refusal, $"{key}='abc'");
                Assert.IsFalse(refusal.Contains('\n'));
            }

            foreach (var key in new[] { "n", "cycles", "steps", "wisp_wcid" })
            {
                StringAssert.Contains(V(Pinnacle(), key + "=abc"), $"{key}='abc'");
                StringAssert.Contains(V(Pinnacle(), key + "=2.5"), "whole number");
            }

            StringAssert.Contains(V(Pinnacle(), "n=0"), "at least 1 wisp");
            StringAssert.Contains(V(Pinnacle(), "n=-3"), "at least 1 wisp");
            Assert.IsNull(V(Pinnacle(), "n=1"));
            Assert.IsNull(V(Pinnacle(), "n=99"), "too many is clamped, not refused");

            Assert.IsNull(V(Pinnacle(), "move=2 hold=2 scale=1.5 pct=0.6"), "real numbers pass");
        }

        [TestMethod]
        public void Validate_RefusesARunLongerThanTheCap_AtExactlyTheBound()
        {
            Assert.AreEqual(600.0, WispSettings.MaxRunSeconds);

            // one step per cycle: cycles x (move 2 + hold 2 + burst 1 + rest 1) = cycles x 6
            Assert.IsNull(V(Pinnacle(), "cycles=100 move=2 hold=2 burst=1 rest=1"), "100 x 6 = 600 exactly");

            var refusal = V(Pinnacle(), "cycles=101 move=2 hold=2 burst=1 rest=1");
            StringAssert.Contains(refusal, "limit is 600");
            Assert.IsFalse(refusal.Contains('\n'));

            // a sweep multiplies by its steps: the Pinnacle has 7 to 9 rows
            Assert.AreEqual(7, WispRules.StepsFor(Pinnacle(), S("dir=n"), "sweep"));
            Assert.IsNotNull(V(Pinnacle(), "cycles=20", "sweep"));
            Assert.IsNull(V(Pinnacle(), "cycles=20", "row"));
            Assert.IsNotNull(V(Pinnacle(), "cycles=20", "mixed"), "mixed can pick a sweep");
            Assert.IsNull(V(Pinnacle(), "cycles=8", "sweep"), "the shipped 8 cycles of a sweep fit");
        }

        [TestMethod]
        public void Validate_RowTakesDirNOrS_ColumnTakesEOrW()
        {
            StringAssert.Contains(V(Pinnacle(), "dir=e", "row"), "north or south");
            StringAssert.Contains(V(Pinnacle(), "dir=w", "row"), "north or south");
            Assert.IsNull(V(Pinnacle(), "dir=n", "row"));
            Assert.IsNull(V(Pinnacle(), "dir=s", "row"));

            StringAssert.Contains(V(Pinnacle(), "dir=n", "column"), "east or west");
            StringAssert.Contains(V(Pinnacle(), "dir=s", "column"), "east or west");
            Assert.IsNull(V(Pinnacle(), "dir=e", "column"));
            Assert.IsNull(V(Pinnacle(), "dir=w", "column"));

            Assert.IsNull(V(Pinnacle(), "dir=e", "mixed"), "mixed just leaves the row out");
            Assert.IsNull(V(Pinnacle(), "dir=n", "diagonal"));
        }

        [TestMethod]
        public void SpawnPillar_RetriesOnceAtTheStand_ThenGivesUp()
        {
            var pillar = new Vector3(10f, -20f, -48f);
            var stand = new Vector3(4.25f, -20f, -51f);
            var attempts = new System.Collections.Generic.List<Vector3>();

            // first attempt works: the stand is never tried
            var host = WispRules.SpawnPillar(p => { attempts.Add(p); return "host"; }, pillar, stand, out var at);
            Assert.AreEqual("host", host);
            Assert.AreEqual(pillar, at);
            CollectionAssert.AreEqual(new[] { pillar }, attempts);

            // first fails, the retry at the stand works
            attempts.Clear();
            host = WispRules.SpawnPillar(p => { attempts.Add(p); return p == stand ? "stand-host" : null; }, pillar, stand, out at);
            Assert.AreEqual("stand-host", host);
            Assert.AreEqual(stand, at);
            CollectionAssert.AreEqual(new[] { pillar, stand }, attempts);

            // both fail: null, exactly two attempts, no third
            attempts.Clear();
            host = WispRules.SpawnPillar(p => { attempts.Add(p); return (string)null; }, pillar, stand, out at);
            Assert.IsNull(host);
            Assert.AreEqual(2, attempts.Count);
        }

        [TestMethod]
        public void PlaceWisp_CostOfOneFormationPassAtN32_OnALongLine()
        {
            // 32 wisps from the south wall to a line across the Pinnacle, a 28 m path each, through the west block
            var currents = new System.Collections.Generic.List<Vector3>();
            var desired = new System.Collections.Generic.List<Vector3>();
            var calls = 0;

            for (var i = 0; i < 32; i++)
            {
                currents.Add(new Vector3(-4f + i * 1.2f, -34f, -51f));
                desired.Add(new Vector3(-4f + i * 1.2f, -6f + 0.01f * i, -51f));
            }

            Func<Vector3, bool> counting = p => { calls++; return HasCell(p); };

            WispRules.Place(currents, desired, counting, 0.5f); // warm up
            calls = 0;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var placed = WispRules.Place(currents, desired, counting, 0.5f);
            sw.Stop();

            Console.WriteLine($"PLACEWISP n=32 28 m paths: {calls} HasCell calls, {sw.Elapsed.TotalMilliseconds:0.000} ms with a trivial predicate, {placed.Count(p => p.Blocked)} blocked");

            Assert.IsTrue(calls < 12000, $"one pass is {calls} lookups");
            Assert.IsTrue(sw.Elapsed.TotalMilliseconds < 250.0, $"{sw.Elapsed.TotalMilliseconds} ms");
        }

        [TestMethod]
        public void Validate_RefusesUnknownFireAndDirWords()
        {
            StringAssert.Contains(V(Pinnacle(), "fire=volley"), "fire='volley'");
            Assert.IsNull(V(Pinnacle(), "fire=sequence"));
            Assert.IsNull(V(Pinnacle(), "fire=RANDOM"));

            StringAssert.Contains(V(Pinnacle(), "dir=up"), "dir='up'");
            Assert.IsNull(V(Pinnacle(), "dir=e"));
            Assert.IsNull(V(Pinnacle(), "dir=random"));
        }

        [TestMethod]
        public void Validate_RefusesANonNumericCentreOrRingRadius()
        {
            StringAssert.Contains(V(Pinnacle(), "cx=abc"), "cx='abc'");
            StringAssert.Contains(V(Pinnacle(), "cy=NaN"), "cy='NaN'");
            StringAssert.Contains(V(Pinnacle(), "ring_radius=big", "ring"), "ring_radius='big'");
            Assert.IsNull(V(Pinnacle(), "cx=10 cy=-15 ring_radius=6", "ring"));
        }

        [TestMethod]
        public void Validate_RefusesACentreOutsideTheRegion()
        {
            var refusal = V(Pinnacle(), "cx=100 cy=-20");

            StringAssert.Contains(refusal, "outside the region");
            StringAssert.Contains(V(Pinnacle(), "cx=10 cy=5"), "outside the region");
            StringAssert.Contains(V(Pinnacle(), "cx=-4.7 cy=-20"), "outside the region");

            Assert.IsNull(V(Pinnacle(), "cx=10 cy=-15"));
            Assert.IsNull(V(Pinnacle(), "cx=-4.5 cy=-20"), "inside is inside, even at the edge");
        }

        [TestMethod]
        public void Validate_RefusesScatterWhosePillarsWouldCoverTooMuch()
        {
            // scatter_cover 0.1 of 887 m2 = 88.7 m2 against one pillar (pi x 5.5^2 = 95 m2)
            var refusal = V(Pinnacle(), "pillar_radius=5 hold=3 scatter_cover=0.1");

            StringAssert.Contains(refusal, "too big for scatter");
            Assert.IsNull(V(Pinnacle(), "pillar_radius=5 hold=3 scatter_cover=0.5"));
            Assert.IsNotNull(V(Pinnacle(), "pillar_radius=5 hold=3 scatter_cover=0.1", "mixed"), "mixed can pick scatter");
            Assert.IsNull(V(Pinnacle(), "pillar_radius=5 hold=3 scatter_cover=0.1", "row"), "a row never covers a region");
        }

        [TestMethod]
        public void Validate_RefusesARingThatDoesNotFit_OrHoldsTooFewWisps()
        {
            // does not fit: the nearest edge is 12.6 m away after the inset
            StringAssert.Contains(V(Pinnacle(), "ring_radius=20", "ring"), "does not fit");
            Assert.IsNull(V(Pinnacle(), "ring_radius=12", "ring"));

            // does not fit round an off-centre point
            StringAssert.Contains(V(Pinnacle(), "cx=0 cy=-20 ring_radius=7", "ring"), "does not fit");

            // too few wisps: ring_radius 3 holds 2 with a 5.5 m spacing
            var refusal = V(Pinnacle(), "ring_radius=3", "ring");
            StringAssert.Contains(refusal, "holds only 2");
            Assert.IsFalse(refusal.Contains('\n'));
            Assert.IsNull(V(Pinnacle(), "ring_radius=3 n=2", "ring"), "two wisps asked for, two fit");
            Assert.IsNull(V(Pinnacle(), "ring_radius=3.2", "ring"), "3.2 m holds 3: floor(pi / asin(5.5 / 6.4)) = 3");

            // a ring that is not the pattern is not checked
            Assert.IsNull(V(Pinnacle(), "ring_radius=3", "row"));

            // mixed: only an EXPLICIT bad ring_radius is a refusal; a default that does not fit just leaves ring out
            StringAssert.Contains(V(Pinnacle(), "ring_radius=3", "mixed"), "holds only");
            Assert.IsNull(V(AdHocRect(12f, 12f), "", "mixed"), "6 m radius: the default ring does not fit, mixed still runs without it");
            CollectionAssert.DoesNotContain(WispRules.Pool(AdHocRect(12f, 12f), S()).ToArray(), "ring");
        }

        [TestMethod]
        public void Validate_RefusesASweepWhoseLineLeavesNoRoomForAGapLane()
        {
            var narrow = AdHocRect(12f, 40f); // arena 8 x 36: rows measured from n/s are 8 m long, 2 x 5.5 = 11 needed

            StringAssert.Contains(V(narrow, "dir=n", "sweep"), "sweep needs a line");
            StringAssert.Contains(V(narrow, "dir=random", "sweep"), "sweep needs a line");
            Assert.IsNull(V(narrow, "dir=e", "sweep"), "from the east the rows run the 36 m way");
            Assert.IsNull(V(narrow, "dir=n", "row"), "only a sweep needs the gap lane");

            CollectionAssert.DoesNotContain(WispRules.Pool(narrow, S()).ToArray(), "sweep");
        }

        [TestMethod]
        public void Validate_RefusalsAreOneLine()
        {
            var cases = new[]
            {
                (AdHocRect(8f, 8f), "", "scatter"),
                (Pinnacle(), "hold=0.1", "scatter"),
                (Pinnacle(), "fire=x", "scatter"),
                (Pinnacle(), "dir=x", "scatter"),
                (Pinnacle(), "cx=x", "scatter"),
                (Pinnacle(), "cx=999", "scatter"),
                (Pinnacle(), "pillar_radius=5 hold=3 scatter_cover=0.1", "scatter"),
                (Pinnacle(), "ring_radius=20", "ring"),
                (Pinnacle(), "ring_radius=3", "ring"),
                (AdHocRect(12f, 40f), "dir=n", "sweep"),
            };

            foreach (var (region, args, pattern) in cases)
            {
                var refusal = V(region, args, pattern);

                Assert.IsNotNull(refusal, $"{args} {pattern}");
                Assert.IsFalse(refusal.Contains('\n') || refusal.Contains('\r'), refusal);
                Assert.IsTrue(refusal.Length < 330, "a refusal is a line, not an essay: " + refusal);
            }
        }

        // ---- fire order and timeline --------------------------------------------------------------------------------

        [TestMethod]
        public void FireOffsets_Unison_IsAllAtOnce()
        {
            CollectionAssert.AreEqual(new double[] { 0, 0, 0, 0 }, WispRules.FireOffsets(WispFire.Unison, 4, 1.5, 0.25, new Random(1)));
        }

        [TestMethod]
        public void FireOffsets_Sequence_IsInFormationOrder_SeqGapApart()
        {
            CollectionAssert.AreEqual(new[] { 0.0, 0.5, 1.0, 1.5 }, WispRules.FireOffsets(WispFire.Sequence, 4, 1.5, 0.5, new Random(1)));
            CollectionAssert.AreEqual(new[] { 0.0, 0.0, 0.0 }, WispRules.FireOffsets(WispFire.Sequence, 3, 1.5, 0.0, new Random(1)), "seq_gap 0 is unison");
        }

        [TestMethod]
        public void FireOffsets_Random_AreWithinTheHoldWindow_ReproduciblePerSeed_AndNotAllEqual()
        {
            var a = WispRules.FireOffsets(WispFire.Random, 8, 1.5, 0.25, new Random(3));
            var b = WispRules.FireOffsets(WispFire.Random, 8, 1.5, 0.25, new Random(3));
            var c = WispRules.FireOffsets(WispFire.Random, 8, 1.5, 0.25, new Random(4));

            CollectionAssert.AreEqual(a, b);
            CollectionAssert.AreNotEqual(a, c);
            Assert.IsTrue(a.All(o => o >= 0.0 && o < 1.5), string.Join(",", a));
            Assert.IsTrue(a.Distinct().Count() > 4);
            Assert.IsTrue(a.Max() > 0.5, "spread over the window, not clumped at zero");
        }

        [TestMethod]
        public void Timeline_NoPillarEverStartsBeforeTheHoldIsOver_WhateverTheFireMode()
        {
            const double move = 2.5, hold = 1.5, burst = 1.5;

            foreach (var fire in new[] { WispFire.Unison, WispFire.Random, WispFire.Sequence })
            {
                for (var seed = 1; seed <= 25; seed++)
                {
                    var offsets = WispRules.FireOffsets(fire, 8, hold, 0.25, new Random(seed));
                    var t = WispRules.Timeline(move, hold, offsets, burst);

                    Assert.AreEqual(move, t.Arrive, 1e-9);
                    Assert.IsTrue(t.BurstAt.All(b => b >= move + hold - 1e-9), $"{fire}: a pillar fired inside the telegraph");
                    Assert.AreEqual(t.BurstAt.Max() + burst, t.End, 1e-9);
                }
            }
        }

        [TestMethod]
        public void Timeline_Numbers()
        {
            var unison = WispRules.Timeline(2.5, 1.5, new[] { 0.0, 0.0 }, 1.5);
            CollectionAssert.AreEqual(new[] { 4.0, 4.0 }, unison.BurstAt.ToArray());
            Assert.AreEqual(5.5, unison.End, 1e-9);

            var seq = WispRules.Timeline(2.5, 1.5, WispRules.FireOffsets(WispFire.Sequence, 4, 1.5, 0.5, null), 1.5);
            CollectionAssert.AreEqual(new[] { 4.0, 4.5, 5.0, 5.5 }, seq.BurstAt.ToArray());
            Assert.AreEqual(7.0, seq.End, 1e-9, "the step ends when the LAST pillar has burned");

            Assert.AreEqual(4.0, WispRules.Timeline(2.5, 1.5, Array.Empty<double>(), 1.5).End, 1e-9);
        }

        // ---- who goes where ----------------------------------------------------------------------------------

        [TestMethod]
        public void Assign_GivesEachTargetTheNearestFreeWisp_AndMinusOneWhenShort()
        {
            var wisps = new[] { new Vector2(0, 0), new Vector2(10, 0), new Vector2(20, 0) };

            CollectionAssert.AreEqual(new[] { 2, 1, 0 }, WispRules.Assign(wisps, new[] { new Vector2(21, 0), new Vector2(9, 0), new Vector2(-1, 0) }));

            var short2 = WispRules.Assign(wisps.Take(2).ToArray(), new[] { new Vector2(0, 0), new Vector2(10, 0), new Vector2(20, 0) });
            CollectionAssert.AreEqual(new[] { 0, 1, -1 }, short2, "a lost wisp leaves the last spot empty; nobody is used twice");
        }

        // ---- PlaceWisp over a formation ----------------------------------------------------------------------

        /// <summary>The Pinnacle's west block: x 5..15, y -25..-15, top -48.00. Below the top it is solid, so no cell.</summary>
        private static bool HasCell(Vector3 p) => !(p.X >= 5f && p.X <= 15f && p.Y >= -25f && p.Y <= -15f && p.Z < -48f);

        [TestMethod]
        public void Place_ABlockedTargetBecomesAStandAndAPillarPoint_AnOpenOneIsUntouched()
        {
            var current = new Vector3(0f, -20f, -51f);
            var inside = new Vector3(10f, -20f, -51f);
            var openFrom = new Vector3(20f, -30f, -51f);
            var open = new Vector3(25f, -30f, -51f);

            var placed = WispRules.Place(new[] { current, openFrom }, new[] { inside, open }, HasCell, 0.5f);

            Assert.IsTrue(placed[0].Blocked);
            Assert.IsTrue(placed[0].Stand.X < 5f, $"the wisp stands short of the block, not at x={placed[0].Stand.X}");
            Assert.AreEqual(4.25f, placed[0].Stand.X, 1e-4, "last open sample 4.75 backed off by the 0.5 clearance");
            Assert.IsTrue(HasCell(placed[0].Stand), "a wisp never stands in a solid");
            Assert.AreEqual(10f, placed[0].Pillar.X, 1e-5);
            Assert.AreEqual(-20f, placed[0].Pillar.Y, 1e-5);
            Assert.AreEqual(-48f, placed[0].Pillar.Z, 1e-4, "the pillar fires from the block's top");
            Assert.IsTrue(HasCell(placed[0].Pillar));
            Assert.IsTrue(placed[0].PillarFound);

            Assert.IsFalse(placed[1].Blocked);
            Assert.AreEqual(open, placed[1].Stand);
            Assert.AreEqual(open, placed[1].Pillar);
        }

        [TestMethod]
        public void Place_EveryStandOfAWholeFormationIsOpen_EveryPillarKeepsItsXY()
        {
            var region = Pinnacle();
            var s = WispSettings.From(HazardArgs.Empty);
            var current = new Vector3(0f, -30f, -51f);
            var hit = 0;

            for (var seed = 1; seed <= 30; seed++)
            {
                foreach (var pattern in new[] { "row", "column", "diagonal", "ring", "scatter" })
                {
                    var f = WispFormations.Plan(region, s, pattern, new Random(seed)).Steps[0];
                    var desired = f.Points.Select(p => new Vector3(p.X, p.Y, -51f)).ToList();
                    var placed = WispRules.Place(desired.Select(_ => current).ToList(), desired, HasCell, 0.5f);

                    for (var i = 0; i < placed.Length; i++)
                    {
                        Assert.IsTrue(HasCell(placed[i].Stand), $"{pattern}: wisp {i} would stand in the block");
                        Assert.AreEqual(desired[i].X, placed[i].Pillar.X, 1e-5, "no safe spot: the pillar is at the true target");
                        Assert.AreEqual(desired[i].Y, placed[i].Pillar.Y, 1e-5);

                        if (placed[i].Blocked)
                            hit++;
                    }
                }
            }

            Assert.IsTrue(hit > 0, "the sample must actually include targets inside the block, or this proves nothing");
        }

        // ---- registry ----------------------------------------------------------------------------------------

        [TestMethod]
        public void Registry_WispsIsTheRealMechanic_NotTheStub()
        {
            Assert.IsTrue(HazardMechanicRegistry.TryCreate("wisps", out var m));

            Assert.IsInstanceOfType(m, typeof(WispSquadMechanic));
            Assert.IsNotInstanceOfType(m, typeof(StubHazardMechanic));
            Assert.AreEqual("wisps", m.Kind);
            CollectionAssert.AreEqual(new[] { "scatter", "row", "column", "diagonal", "ring", "sweep", "mixed" }, m.Patterns.ToArray());

            CollectionAssert.AreEquivalent(new[] { HazardDefaults.EmberWispWcid, HazardDefaults.EffectHostWcid }, m.RequiredWcids(HazardArgs.Empty).ToArray());
            CollectionAssert.Contains(m.RequiredWcids(HazardArgs.Parse("wisp_wcid=1234")).ToArray(), 1234u);

            Assert.IsNull(m.Validate(Pinnacle(), HazardArgs.Parse(HazardDefaults.Wisps), "ring"));
            StringAssert.Contains(m.Validate(Pinnacle(), HazardArgs.Parse("hold=0.1"), "ring"), "hold");
        }

        // ---- MaxCyclesWithin (HAZARDS-PLAN S0) ---------------------------------------------------------------------

        [TestMethod]
        public void MaxCyclesWithin_IsTheWholeCyclesThatFit_AndAgreesWithValidate()
        {
            var args = HazardArgs.Parse("move=2 hold=2 burst=1 rest=1"); // 6 s per cycle, one step per cycle for scatter

            Assert.AreEqual(100, WispRules.MaxCyclesWithin(Pinnacle(), args, "scatter"), "600 / 6");
            Assert.AreEqual(10, WispRules.MaxCyclesWithin(Pinnacle(), args, "scatter", 60.0));
            Assert.AreEqual(10, WispRules.MaxCyclesWithin(Pinnacle(), args, "scatter", 65.9), "floor");
            Assert.AreEqual(0, WispRules.MaxCyclesWithin(Pinnacle(), args, "scatter", 5.9), "not even one fits");

            // Validate accepts exactly that many cycles and refuses one more
            var n = WispRules.MaxCyclesWithin(Pinnacle(), args, "scatter");
            Assert.IsNull(V(Pinnacle(), $"move=2 hold=2 burst=1 rest=1 cycles={n}"));
            StringAssert.Contains(V(Pinnacle(), $"move=2 hold=2 burst=1 rest=1 cycles={n + 1}"), "limit is 600");
        }

        [TestMethod]
        public void MaxCyclesWithin_NullPatternIsScatter_ASweepDividesByItsSteps_BadInputIsZero()
        {
            var args = HazardArgs.Parse("move=2 hold=2 burst=1 rest=1 dir=n");

            Assert.AreEqual(WispRules.MaxCyclesWithin(Pinnacle(), args, "scatter"), WispRules.MaxCyclesWithin(Pinnacle(), args, null));

            var steps = WispRules.StepsFor(Pinnacle(), S("dir=n"), "sweep");
            Assert.AreEqual(7, steps);
            Assert.AreEqual((int)Math.Floor(600.0 / (steps * 6.0)), WispRules.MaxCyclesWithin(Pinnacle(), args, "sweep"));

            Assert.AreEqual(0, WispRules.MaxCyclesWithin(null, args, "scatter"));
            Assert.AreEqual(0, WispRules.MaxCyclesWithin(Pinnacle(), args, "scatter", 0.0));
            Assert.AreEqual(0, WispRules.MaxCyclesWithin(Pinnacle(), args, "scatter", -5.0));
            Assert.AreEqual(0, WispRules.MaxCyclesWithin(Pinnacle(), args, "scatter", double.NaN));
            Assert.IsTrue(WispRules.MaxCyclesWithin(Pinnacle(), null, "scatter") > 0, "null args read as the defaults");
        }
    }
}
