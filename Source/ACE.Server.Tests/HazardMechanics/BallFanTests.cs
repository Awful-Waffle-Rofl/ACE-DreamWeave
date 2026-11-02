using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using ACE.Server.HazardMechanics;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.HazardMechanics
{
    /// <summary>The aimed fan (BallPatterns.PlanFan, FanAngles, PickTarget) and its sidestep Validate rule. Pure.</summary>
    [TestClass]
    public class BallFanTests
    {
        private const double Deg = Math.PI / 180.0;

        private static HazardRegion Pinnacle() => HazardRegionPresets.Pinnacle(0);

        private static HazardRegion Circle() => HazardRegion.Circle("circle", 0x0069, 0, false, 15f, -20f, 14f, HazardGroundMode.Plane, -47.75f, null, 2f, null, 2f, 6f);

        private static HazardRegion AdHoc()
        {
            var r = HazardRegion.AdHoc(0xA9B4, 0, true, new Vector3(96f, 96f, 30f), HazardShapeSpec.Rect(40f, 30f), 0f, out var error);
            Assert.IsNull(error);
            return r;
        }

        private static BallParams P(string args) => BallRules.Read(HazardArgs.Merge(HazardArgs.Parse(args), HazardArgs.Parse(HazardDefaults.Balls)), BallPatterns.Fan);

        private static string Validate(HazardRegion region, string args)
            => BallRules.Validate(region, HazardArgs.Merge(HazardArgs.Parse(args), HazardArgs.Parse(HazardDefaults.Balls)), BallPatterns.Fan);

        [TestMethod]
        public void Angles_AtCount1_3_5_AreEvenlySpreadAcrossTheSpread_CentredOnTheAim()
        {
            var aim = 0.7;

            CollectionAssert.AreEqual(new[] { aim }, BallPatterns.FanAngles(aim, 1, 30));

            var three = BallPatterns.FanAngles(aim, 3, 30);
            Assert.AreEqual(aim - 15 * Deg, three[0], 1e-9);
            Assert.AreEqual(aim, three[1], 1e-9, "the middle ball goes straight at the target");
            Assert.AreEqual(aim + 15 * Deg, three[2], 1e-9);

            var five = BallPatterns.FanAngles(aim, 5, 30);
            var expected = new[] { -15.0, -7.5, 0.0, 7.5, 15.0 };

            for (var i = 0; i < 5; i++)
                Assert.AreEqual(aim + expected[i] * Deg, five[i], 1e-9, "ball " + i);
        }

        [TestMethod]
        public void SpreadAndCount_AreClamped_AndDefaultToThreeAtThirty()
        {
            Assert.AreEqual(3, P("").Count);
            Assert.AreEqual(30f, P("").Spread);
            Assert.AreEqual(7, P("count=99").Count);
            Assert.AreEqual(1, P("count=0").Count);
            Assert.AreEqual(120f, P("spread=500").Spread);
            Assert.AreEqual(0f, P("spread=-5").Spread);

            // the pure angles clamp too: a 500 degree request fans +-60 degrees, not +-250
            var wide = BallPatterns.FanAngles(0, 3, 500);
            Assert.AreEqual(-60 * Deg, wide[0], 1e-9);
            Assert.AreEqual(60 * Deg, wide[2], 1e-9);

            // spread 0: every ball on the aim line
            Assert.IsTrue(BallPatterns.FanAngles(1.0, 4, 0).All(a => a == 1.0));
        }

        [TestMethod]
        public void Plan_OnRectCircleAndAdHoc_AimsTheMiddleBallAtTheTarget_AndStopsAtTheInsetEdge()
        {
            foreach (var (name, region, target) in new[]
            {
                ("pinnacle", Pinnacle(), new Vector2(30f, -10f)),
                ("circle", Circle(), new Vector2(20f, -26f)),
                ("adhoc", AdHoc(), new Vector2(110f, 100f)),
            })
            {
                var p = P("count=5 spread=40");
                var wave = BallPatterns.PlanFan(region, p, target);
                var o = region.Center;
                var aim = Math.Atan2(target.Y - o.Y, target.X - o.X);

                Assert.AreEqual(5, wave.Routes.Count, name);

                for (var i = 0; i < 5; i++)
                {
                    var r = wave.Routes[i];
                    Assert.AreEqual(o.X, r.From.X, 1e-3f, name);
                    Assert.AreEqual(o.Y, r.From.Y, 1e-3f, name);
                    Assert.AreEqual(p.Inset, region.DistanceToEdge(r.To.X, r.To.Y), 0.02f, name + " stops at the inset edge");

                    var angle = Math.Atan2(r.To.Y - r.From.Y, r.To.X - r.From.X);
                    Assert.AreEqual(aim + 40 * Deg * (i / 4.0 - 0.5), angle, 1e-3, $"{name} ball {i}");
                }

                Assert.IsTrue(wave.Markers.Count > 0, name + ": the aim line is telegraphed");
                Assert.IsTrue(wave.Markers.All(m => Math.Abs(Math.Atan2(m.Y - o.Y, m.X - o.X) - aim) < 1e-3), name + ": markers lie on the aim line");
            }
        }

        [TestMethod]
        public void Origin_IsCxCy_Else_TheRegionCentre()
        {
            var region = Pinnacle();
            var target = new Vector2(30f, -10f);

            var centred = BallPatterns.PlanFan(region, P(""), target);
            Assert.AreEqual(region.CenterX, centred.Routes[0].From.X, 1e-3f);

            var moved = BallPatterns.PlanFan(region, P("cx=3 cy=-30"), target);
            Assert.IsTrue(moved.Routes.All(r => Math.Abs(r.From.X - 3f) < 1e-3f && Math.Abs(r.From.Y + 30f) < 1e-3f), "the code caller passes the origin as cx/cy args");
        }

        /// <summary>
        /// Bug 2026-10-18: /hazard balls pattern=fan at=me target=nearest puts the origin ON the only player, so the
        /// aim vector is centimetres long. The aim must never come from that noise: it follows the target's facing,
        /// then the region centre, then +x - and every ball still rolls a full lane to the inset edge.
        /// </summary>
        [TestMethod]
        public void DegenerateAim_TargetOnTheOrigin_FollowsTheTargetsFacing_ThenTheCentre_ThenPlusX()
        {
            var region = Pinnacle(); // centre (15, -20)
            var origin = new Vector2(22.59f, -17.107f); // the owner's at=me spot
            var reach = P("").Reach;
            var drift = origin + new Vector2(-0.03f, 0.02f); // the player shuffled 3.6 cm since at=me was read

            var real = BallPatterns.FanAim(origin, origin + new Vector2(0f, reach + 0.01f), null, region.Center, reach);
            Assert.AreEqual(FanAimSource.Target, real.Source, "control: one reach away is a real aim");
            Assert.IsFalse(real.Degenerate);
            Assert.AreEqual("", real.Note);

            // facing north (+y): the fan goes north, not along the 3.6 cm drift (which points north-west)
            var faced = BallPatterns.FanAim(origin, drift, new Vector2(0f, 1f), region.Center, reach);
            Assert.AreEqual(FanAimSource.Facing, faced.Source);
            Assert.IsTrue(faced.Degenerate);
            Assert.AreEqual(Math.PI / 2, faced.Angle, 1e-6);
            StringAssert.Contains(faced.Note, "facing");

            // no facing: towards the region centre
            var toCentre = Math.Atan2(region.CenterY - origin.Y, region.CenterX - origin.X);
            var none = BallPatterns.FanAim(origin, drift, null, region.Center, reach);
            Assert.AreEqual(FanAimSource.RegionCentre, none.Source);
            Assert.AreEqual(toCentre, none.Angle, 1e-6);

            var zero = BallPatterns.FanAim(origin, drift, Vector2.Zero, region.Center, reach);
            Assert.AreEqual(FanAimSource.RegionCentre, zero.Source, "a zero facing is no facing");
            Assert.IsFalse(zero.Note.Contains("facing"), $"the announcement must not claim a facing it did not use: '{zero.Note}'");

            // origin on the centre too: +x
            var plusX = BallPatterns.FanAim(region.Center, region.Center, null, region.Center, reach);
            Assert.AreEqual(FanAimSource.PlusX, plusX.Source);
            Assert.AreEqual(0.0, plusX.Angle, 1e-9);

            // control: a target farther than one reach is aimed at directly, whatever it faces
            var far = new Vector2(30f, -30f);
            var direct = BallPatterns.FanAim(origin, far, new Vector2(0f, 1f), region.Center, reach);
            Assert.AreEqual(FanAimSource.Target, direct.Source);
            Assert.AreEqual(Math.Atan2(far.Y - origin.Y, far.X - origin.X), direct.Angle, 1e-6);
        }

        [TestMethod]
        public void DegenerateAim_NaNFacing_IsNoFacing_AndIsNotAnnouncedAsOne()
        {
            var region = Pinnacle();
            var origin = new Vector2(22.59f, -17.107f);
            var reach = P("").Reach;

            foreach (var facing in new[] { new Vector2(float.NaN, 1f), new Vector2(0f, float.NaN), new Vector2(float.PositiveInfinity, 0f) })
            {
                var aim = BallPatterns.FanAim(origin, origin, facing, region.Center, reach);

                Assert.AreEqual(FanAimSource.RegionCentre, aim.Source, $"facing {facing}");
                Assert.IsTrue(double.IsFinite(aim.Angle), $"facing {facing}: angle {aim.Angle}");
                Assert.IsFalse(aim.Note.Contains("facing"), $"facing {facing}: '{aim.Note}'");
            }
        }
        [TestMethod]
        public void DegenerateAim_PlanFan_StillRollsEveryBallAFullLane_AlongTheFacing()
        {
            var region = Pinnacle();
            var p = P("cx=22.59 cy=-17.107 spread=50");
            var origin = new Vector2(22.59f, -17.107f);

            var wave = BallPatterns.PlanFan(region, p, origin, new Vector2(-1f, 0f)); // target exactly on the origin, facing west

            Assert.AreEqual(3, wave.Routes.Count);

            foreach (var r in wave.Routes)
            {
                Assert.IsTrue(r.Length >= 10f, $"a ball must travel, not sit on the origin: {r.From} -> {r.To} is {r.Length:0.00} m");
                Assert.AreEqual(p.Inset, region.DistanceToEdge(r.To.X, r.To.Y), 0.02f, "rolls to the inset edge");
            }

            var middle = wave.Routes[1];
            Assert.AreEqual(Math.PI, Math.Abs(Math.Atan2(middle.To.Y - middle.From.Y, middle.To.X - middle.From.X)), 1e-3, "the middle ball goes along the facing (west)");
        }

        [TestMethod]
        public void PickTarget_NoPlayer_IsMinusOne_Nearest_IsNearest_Random_UsesTheRng()
        {
            var origin = new Vector2(15f, -20f);
            var players = new List<Vector2> { new Vector2(30f, -20f), new Vector2(17f, -20f), new Vector2(0f, -20f) };

            Assert.AreEqual(-1, BallPatterns.PickTarget(new List<Vector2>(), origin, "random", new Random(1)));
            Assert.AreEqual(-1, BallPatterns.PickTarget(null, origin, "nearest", new Random(1)));
            Assert.AreEqual(1, BallPatterns.PickTarget(players, origin, "nearest", new Random(1)));

            var seen = new HashSet<int>();

            for (var seed = 1; seed <= 40; seed++)
                seen.Add(BallPatterns.PickTarget(players, origin, "random", new Random(seed)));

            CollectionAssert.AreEquivalent(new[] { 0, 1, 2 }, seen.ToArray(), "random reaches every player (nearest would always give 1)");
        }

        [TestMethod]
        public void Validate_SidestepRule_AcceptsTheDefaults_AndRefusesAFanTooWideToSidestep()
        {
            Assert.IsNull(Validate(Pinnacle(), ""));
            Assert.IsNull(Validate(Pinnacle(), "count=1 spread=0"));
            Assert.IsNull(Validate(Pinnacle(), "count=7 spread=120"));

            // FanLead = ((d sin t + reach) / cos t + 0.5) / speed, t = spread / 2: 30 degrees at 20 m with reach 2.3 -> 2.75 s
            Assert.AreEqual(2.75, BallRules.FanLead(30, 20, 2.3, 3.0), 0.01);
            Assert.AreEqual((2.3 + 0.5) / 3, BallRules.FanLead(0, 20, 2.3, 3.0), 1e-9, "spread 0 needs only one reach");

            // a 190 m square: 120 degrees needs ((186 x 0.866 + 2.3) / 0.5 + 0.5) / 3 = 109 s, over the 30 s cap; 30 degrees needs 17.6 s
            var huge = HazardRegion.Rect("huge", 0xA9B4, 0, true, 1f, 191f, 1f, 191f, HazardGroundMode.Terrain, 0, null, 0, null, 2, 6);
            StringAssert.Contains(Validate(huge, "spread=120"), "too wide to sidestep");
            Assert.IsNull(Validate(huge, "spread=30"));
        }

        [TestMethod]
        public void Validate_RefusesABadTargetOrOrigin_AndTheLeadIsRaisedToTheSidestepTime()
        {
            StringAssert.Contains(Validate(Pinnacle(), "target=everyone"), "random or nearest");
            Assert.IsNull(Validate(Pinnacle(), "target=nearest"));
            StringAssert.Contains(Validate(Pinnacle(), "cx=200 cy=0"), "outside the region");

            var wide = P("spread=120 lead=0.5");
            var lead = BallRules.EffectiveLead(Pinnacle(), BallPatterns.Fan, wide);
            Assert.IsTrue(lead > 5.0, $"a 120 degree fan raises a 0.5 s lead to {lead:0.0} s");
            Assert.IsTrue(BallRules.EffectiveLead(Pinnacle(), BallPatterns.Fan, P("spread=0 lead=0.5")) < lead, "control: a narrow fan needs less");
        }
    }
}
