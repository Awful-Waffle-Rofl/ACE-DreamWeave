using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;

using ACE.Server.HazardMechanics;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.HazardMechanics
{
    /// <summary>
    /// The no-escape bounds Validate enforces (BallRules), the fair-lead floor, the wave clock, the registry wiring
    /// and the at=me command parse. Each guard is tested on both sides of its boundary and against a substituted
    /// input, so removing the guard (or hard-coding its value) fails a test. Pure: no PropertyManager is touched.
    /// </summary>
    [TestClass]
    public class BallRulesTests
    {
        private static HazardRegion Pinnacle() => HazardRegionPresets.Pinnacle(0);

        private static string Validate(HazardRegion region, string args, string pattern)
            => BallRules.Validate(region, HazardArgs.Merge(HazardArgs.Parse(args), HazardArgs.Parse(HazardDefaults.Balls)), pattern);

        private static BallParams P(string args, string pattern) => BallRules.Read(HazardArgs.Merge(HazardArgs.Parse(args), HazardArgs.Parse(HazardDefaults.Balls)), pattern);

        // ---- defaults and wiring ---------------------------------------------------------------------------------

        [TestMethod]
        public void ShippedDefaults_ValidateOnThePinnacle_ForEveryPattern()
        {
            foreach (var pattern in BallPatterns.All)
                Assert.IsNull(Validate(Pinnacle(), "", pattern), pattern);
        }

        [TestMethod]
        public void SettingsString_HasNoCount_BecauseTheDefaultDiffersPerPattern()
        {
            Assert.IsFalse(HazardArgs.Parse(HazardDefaults.Balls).Has("count"), "a count in the settings string would override both per-pattern defaults");
            Assert.AreEqual(HazardDefaults.BallsRingCount, P("", BallPatterns.RingOut).Count);
            Assert.AreEqual(HazardDefaults.BallsRandomCount, P("", BallPatterns.Random).Count);
            Assert.AreEqual(11, P("count=11", BallPatterns.Random).Count);
        }

        [TestMethod]
        public void Registry_BallsIsTheRealMechanic_WithTheSevenPatternsRingOutFirst()
        {
            Assert.IsTrue(HazardMechanicRegistry.TryCreate("balls", out var m));
            Assert.IsInstanceOfType(m, typeof(RollingBallMechanic));
            Assert.IsNotInstanceOfType(m, typeof(StubHazardMechanic));
            CollectionAssert.AreEqual(new[] { "ring-out", "ring-in", "row", "random", "cross", "fan", "ring-alt" }, m.Patterns.ToArray());

            Assert.IsTrue(HazardManager.TryResolvePattern(m, null, out var pattern, out _));
            Assert.AreEqual("ring-out", pattern);
            Assert.IsTrue(HazardManager.TryResolvePattern(m, "CROSS", out pattern, out _));
            Assert.AreEqual("cross", pattern);
            Assert.IsFalse(HazardManager.TryResolvePattern(m, "zigzag", out _, out _));

            CollectionAssert.Contains(m.RequiredWcids(HazardArgs.Empty).ToArray(), HazardDefaults.RollingDeathRedWcid);
            Assert.AreEqual(1006951u, HazardDefaults.RollingDeathRedWcid);
        }

        // ---- row / cross: the gap ----------------------------------------------------------------------------------

        [TestMethod]
        public void Gap_BelowTwoReachPlusHalfAMetre_IsRefused_AtOrAboveIsAccepted()
        {
            // W = 2 x (1.8 + 0.5) + 0.5 = 5.1
            foreach (var pattern in new[] { BallPatterns.Row, BallPatterns.Cross })
            {
                StringAssert.Contains(Validate(Pinnacle(), "gap=5.0", pattern), "too narrow");
                Assert.IsNull(Validate(Pinnacle(), "gap=5.3", pattern), pattern);
            }
        }

        [TestMethod]
        public void Gap_Floor_FollowsTheBallAndPlayerRadius_NotAConstant()
        {
            // gap 6 passes with the stock radii (W 5.1) ...
            Assert.IsNull(Validate(Pinnacle(), "gap=6", BallPatterns.Row));

            // ... and is refused once either radius grows (W 6.5): a hard-coded 5.1 would let both through
            StringAssert.Contains(Validate(Pinnacle(), "gap=6 ball_radius=2.5", BallPatterns.Row), "too narrow");
            StringAssert.Contains(Validate(Pinnacle(), "gap=6 player_radius=1.2", BallPatterns.Cross), "too narrow");

            // a smaller ball lowers the floor: 4.6 would have been refused at the stock radius
            Assert.IsNull(Validate(Pinnacle(), "gap=4.6 ball_radius=1.0", BallPatterns.Row));
        }

        [TestMethod]
        public void Gap_MustFitTheSide_AndEverySideARandomDirCouldPick()
        {
            // Pinnacle lateral spans: north/south (x) 36.9 m, east/west (y) 26.9 m; a gap needs gap + 2 x 2.3 within them
            Assert.IsNull(Validate(Pinnacle(), "gap=30 dir=n", BallPatterns.Row), "34.6 <= 36.9");
            StringAssert.Contains(Validate(Pinnacle(), "gap=30 dir=e", BallPatterns.Row), "does not fit the east side");
            StringAssert.Contains(Validate(Pinnacle(), "gap=30 dir=random", BallPatterns.Row), "does not fit", "random may pick east or west");

            // a cross checks only its own two sides
            Assert.IsNull(Validate(Pinnacle(), "gap=20 dirs=ne", BallPatterns.Cross));
            StringAssert.Contains(Validate(Pinnacle(), "gap=25 dirs=ne", BallPatterns.Cross), "does not fit the east side");
        }

        [TestMethod]
        public void BadDirDirsAndCentre_AreRefusedInOneLine()
        {
            StringAssert.Contains(Validate(Pinnacle(), "dir=up", BallPatterns.Row), "dir 'up'");
            StringAssert.Contains(Validate(Pinnacle(), "dirs=nn", BallPatterns.Cross), "dirs 'nn'");
            StringAssert.Contains(Validate(Pinnacle(), "dirs=ns", BallPatterns.Cross), "adjacent");
            StringAssert.Contains(Validate(Pinnacle(), "cx=5", BallPatterns.RingOut), "together");
            StringAssert.Contains(Validate(Pinnacle(), "cx=abc cy=1", BallPatterns.RingOut), "numbers");

            foreach (var accepted in new[] { "dir=n", "dir=S", "dir=random" })
                Assert.IsNull(Validate(Pinnacle(), accepted, BallPatterns.Row), accepted);

            foreach (var accepted in new[] { "dirs=ne", "dirs=WS", "dirs=random" })
                Assert.IsNull(Validate(Pinnacle(), accepted, BallPatterns.Cross), accepted);

            Assert.IsFalse(Validate(Pinnacle(), "dir=up", BallPatterns.Row).Contains('\n'), "one line");
        }

        // ---- rings ---------------------------------------------------------------------------------------------------

        [TestMethod]
        public void Ring_ChordAtSixTenthsOfTheShortestRay_MustReachTheMinGap()
        {
            // Pinnacle centre: shortest ray 14.6 - 2 = 12.6, r_esc = 7.56, W = 5.1: chord(9) = 5.17 ok, chord(10) = 4.67 not
            Assert.AreEqual(9, BallRules.MaxRingCount(7.56, 5.1));

            foreach (var pattern in new[] { BallPatterns.RingOut, BallPatterns.RingIn })
            {
                Assert.IsNull(Validate(Pinnacle(), "count=9", pattern), pattern);

                var refused = Validate(Pinnacle(), "count=10", pattern);
                StringAssert.Contains(refused, "count <= 9");
                StringAssert.Contains(refused, "4.67");
            }
        }

        [TestMethod]
        public void Ring_Bound_ShrinksWithTheRegion_AndWithTheBallSize()
        {
            var small = HazardRegion.Rect("small", 0x0069, 0, false, 0, 24, 0, 24, HazardGroundMode.Plane, 0, null, 0, null, 2, 6);

            // shortest ray 12 - 2 = 10, r_esc = 6: chord(8) = 4.59 < 5.1 but 6 balls give 6.0
            StringAssert.Contains(Validate(small, "count=8", BallPatterns.RingOut), "count <= ");
            Assert.IsNull(Validate(small, "count=6", BallPatterns.RingOut));

            // a bigger ball raises W (7.1 at radius 3.05): the stock 8 balls now fail on the Pinnacle
            Assert.IsNull(Validate(Pinnacle(), "count=8", BallPatterns.RingOut));
            StringAssert.Contains(Validate(Pinnacle(), "count=8 ball_radius=3.05", BallPatterns.RingOut), "count <= ");
        }

        [TestMethod]
        public void Ring_NeedsThreeBalls_ACentreInsideTheRegion_AndOutsideAnyExclusion()
        {
            StringAssert.Contains(Validate(Pinnacle(), "count=2", BallPatterns.RingOut), "count >= 3");
            StringAssert.Contains(Validate(Pinnacle(), "cx=200 cy=-20", BallPatterns.RingOut), "outside the region");
            Assert.IsNull(Validate(Pinnacle(), "cx=15 cy=-20", BallPatterns.RingIn));

            var padded = HazardRegion.Rect("padded", 0x0069, 0, false, -4.6f, 34.6f, -34.6f, -5.4f, HazardGroundMode.Plane, -47.75f, null, 0, null, 2, 6,
                new[] { HazardExclusion.Rect(10f, 20f, -30f, -10f) });

            StringAssert.Contains(Validate(padded, "", BallPatterns.RingOut), "exclusion");
            Assert.IsNull(Validate(padded, "count=3 cx=3 cy=-20", BallPatterns.RingOut), "the same ring centred off the pad is fine");
        }

        [TestMethod]
        public void Ring_ChordAndLaneRadius_AreTheDocumentedFormulas()
        {
            Assert.AreEqual(2 * 7.56 * Math.Sin(Math.PI / 9), BallRules.ChordAt(7.56, 9), 1e-9);
            Assert.AreEqual(5.1 / (2 * Math.Sin(Math.PI / 8)), BallRules.LaneRadius(5.1, 8), 1e-9);
            Assert.AreEqual(0, BallRules.MaxRingCount(2.0, 5.1), "even two balls do not fit in a 2 m radius");
        }

        // ---- random --------------------------------------------------------------------------------------------------

        [TestMethod]
        public void Random_ConcurrentLanes_AreBoundedByThreeQuartersOfTheNarrowestSide()
        {
            // Pinnacle: L_max 35.2, S_min 26.9, 2 reach 4.6 -> limit 4.386; m(7 per 20 s at 3 m/s) = 4.11, m(8) = 4.69
            Assert.IsNull(Validate(Pinnacle(), "count=7 window=20 speed=3", BallPatterns.Random));

            var refused = Validate(Pinnacle(), "count=8 window=20 speed=3", BallPatterns.Random);
            StringAssert.Contains(refused, "launch rate is too high");
            StringAssert.Contains(refused, "fits 4.4");
        }

        [TestMethod]
        public void Random_Bound_RespondsToWindowSpeedAndLaneWidth()
        {
            Assert.IsNotNull(Validate(Pinnacle(), "count=20 window=20 speed=3", BallPatterns.Random));
            Assert.IsNull(Validate(Pinnacle(), "count=20 window=120 speed=3", BallPatterns.Random), "a longer window spreads the launches");
            Assert.IsNull(Validate(Pinnacle(), "count=20 window=20 speed=12", BallPatterns.Random), "faster balls leave sooner");
            Assert.IsNotNull(Validate(Pinnacle(), "count=7 window=20 speed=3 ball_radius=3", BallPatterns.Random), "wider lanes fill the room sooner");
        }

        [TestMethod]
        public void NarrowRegion_And_OversizedInset_AreRefused()
        {
            var thin = HazardRegion.Rect("thin", 0x0069, 0, false, 0, 40, 0, 6, HazardGroundMode.Plane, 0, null, 0, null, 2, 6);
            StringAssert.Contains(Validate(thin, "", BallPatterns.Random), "narrowest side");

            // the inset eats the whole travel axis: nothing can roll
            var square = HazardRegion.Rect("square", 0x0069, 0, false, 0, 20, 0, 20, HazardGroundMode.Plane, 0, null, 0, null, 2, 6);
            StringAssert.Contains(Validate(square, "edge_inset=10 dir=n", BallPatterns.Row), "too small");
            Assert.IsNull(Validate(square, "edge_inset=2 dir=n", BallPatterns.Row), "control: the same row at the stock inset is fine");
        }

        // ---- the lead --------------------------------------------------------------------------------------------------

        [TestMethod]
        public void FairLead_IsTheDocumentedFormulaPerPattern_AndRaisesAShortLeadNeverRefusesIt()
        {
            var region = Pinnacle();

            // row: widest side 36.9 m, gap 6: (36.9 / 2 - 6 / 2) / 3 = 5.15 s
            var row = P("", BallPatterns.Row);
            Assert.AreEqual(5.15, BallRules.FairLead(region, BallPatterns.Row, row), 0.01);
            Assert.AreEqual(5.15, BallRules.EffectiveLead(region, BallPatterns.Row, row), 0.01, "the 2 s default is raised");
            Assert.AreEqual(10.0, BallRules.EffectiveLead(region, BallPatterns.Row, P("lead=10", BallPatterns.Row)), 1e-9, "a longer lead is kept");

            // one specific side: east is narrower (26.9 m): (13.45 - 3) / 3 = 3.48 s
            Assert.AreEqual(3.48, BallRules.FairLead(region, BallPatterns.Row, P("dir=e", BallPatterns.Row)), 0.01);

            // ring of 8: lane radius 5.1 / (2 sin 22.5 deg) = 6.67 m, / 3 = 2.22 s
            Assert.AreEqual(2.22, BallRules.FairLead(region, BallPatterns.RingOut, P("", BallPatterns.RingOut)), 0.01);

            // random: one reach to step out of a lane, 2.3 / 3 = 0.77 s, so the 2 s default stands
            Assert.AreEqual(0.77, BallRules.FairLead(region, BallPatterns.Random, P("", BallPatterns.Random)), 0.01);
            Assert.AreEqual(2.0, BallRules.EffectiveLead(region, BallPatterns.Random, P("", BallPatterns.Random)), 1e-9);

            // never under the framework's 0.5 s floor (the MoveLeg spawn-tick deferral)
            Assert.IsTrue(BallRules.EffectiveLead(region, BallPatterns.Random, P("lead=0.01", BallPatterns.Random)) >= HazardDefaults.BallsMinLead);
        }

        [TestMethod]
        public void FairLead_LetsAWalkingPlayerFromTheMiddleReachTheWorstGap_ANaiveLeadDoesNot()
        {
            var region = Pinnacle();
            var p = P("dir=n", BallPatterns.Row);
            var (lo, hi) = BallPatterns.LateralRange(region, BallSide.North, p);
            var middle = (lo + hi) * 0.5f;
            var walk = (float)p.EscapeSpeed;
            var lead = (float)BallRules.EffectiveLead(region, BallPatterns.Row, p);

            // the worst gaps are the two ends of the side; the nearest gap edge is gap away from the end
            foreach (var edgeOfGap in new[] { lo + p.Gap, hi - p.Gap })
                Assert.IsTrue(Math.Abs(middle - edgeOfGap) <= walk * lead, $"a walker needs {Math.Abs(middle - edgeOfGap) / walk:0.0} s but the lead is {lead:0.0}");

            // control: the stock 2 s lead alone would not get them there
            Assert.IsTrue(Math.Abs(middle - (lo + p.Gap)) > walk * 2.0f, "the check must be able to fail");
        }

        // ---- the wave clock ------------------------------------------------------------------------------------------

        [TestMethod]
        public void WaveSeconds_IsDelayPlusLeadPlusLegPlusOneSecondSettle_OfTheLastBall()
        {
            var wave = new BallWave();
            wave.Routes.Add(new BallRoute(new Vector2(0, 0), new Vector2(12, 0), 0.0, "a"));
            wave.Routes.Add(new BallRoute(new Vector2(0, 0), new Vector2(6, 0), 5.0, "b"));

            // a: 0 + 2 + 12 / 3 = 6;  b: 5 + 2 + 6 / 3 = 9;  + 1 settle
            Assert.AreEqual(10.0, BallRules.WaveSeconds(wave, 3f, 2.0), 1e-6);

            // a very short leg still takes the 0.5 s the framework gives it
            var tiny = new BallWave();
            tiny.Routes.Add(new BallRoute(new Vector2(0, 0), new Vector2(0.5f, 0), 0.0, "t"));
            Assert.AreEqual(2.0 + 0.5 + 1.0, BallRules.WaveSeconds(tiny, 3f, 2.0), 1e-6);

            Assert.AreEqual(1.0, BallRules.WaveSeconds(new BallWave(), 3f, 2.0), 1e-6);
        }

        // ---- at=me ----------------------------------------------------------------------------------------------------

        [TestMethod]
        public void AtMe_FillsCxCyFromTheCaller_AndIsNotPassedToTheMechanic()
        {
            Assert.IsTrue(HazardCommandRequest.TryParse(new[] { "balls", "ring-out", "at=me", "count=6" }, out var request, out var error), error);

            Assert.IsTrue(request.AtCaller);
            Assert.IsFalse(request.Args.Has("at"), "at is reserved");
            Assert.IsFalse(request.Args.Has("cx"));

            var args = request.ArgsFor(new Vector3(10.5f, -20.25f, 3f));
            Assert.AreEqual("10.5", args.GetString("cx"));
            Assert.AreEqual("-20.25", args.GetString("cy"));
            Assert.AreEqual("6", args.GetString("count"));

            var centred = BallRules.Read(HazardArgs.Merge(args, HazardArgs.Empty), BallPatterns.RingOut).Centre.Value;
            Assert.AreEqual(10.5f, centred.X, 1e-4f);
            Assert.AreEqual(-20.25f, centred.Y, 1e-4f);
        }

        [TestMethod]
        public void AtMe_Control_WithoutItArgsForIsTheArgsThemselves_AndBadUseIsRefused()
        {
            Assert.IsTrue(HazardCommandRequest.TryParse(new[] { "balls", "ring-out", "count=6" }, out var plain, out _));
            Assert.IsFalse(plain.AtCaller);
            Assert.AreSame(plain.Args, plain.ArgsFor(new Vector3(1, 2, 3)));

            Assert.IsFalse(HazardCommandRequest.TryParse(new[] { "balls", "at=you" }, out _, out var e1));
            StringAssert.Contains(e1, "at must be 'me'");

            Assert.IsFalse(HazardCommandRequest.TryParse(new[] { "balls", "at=me", "cx=3", "cy=4" }, out _, out var e2));
            StringAssert.Contains(e2, "not both");
        }

        [TestMethod]
        public void AtMe_ACallerOutsideThePresetRegion_IsRefusedByValidate()
        {
            Assert.IsTrue(HazardCommandRequest.TryParse(new[] { "balls", "ring-out", "at=me" }, out var request, out _));

            var outside = BallRules.Validate(Pinnacle(), HazardArgs.Merge(request.ArgsFor(new Vector3(200f, 5f, 0f)), HazardArgs.Parse(HazardDefaults.Balls)), BallPatterns.RingOut);
            StringAssert.Contains(outside, "outside the region");

            var inside = BallRules.Validate(Pinnacle(), HazardArgs.Merge(request.ArgsFor(new Vector3(15f, -20f, 0f)), HazardArgs.Parse(HazardDefaults.Balls)), BallPatterns.RingOut);
            Assert.IsNull(inside);
        }
    }
}
