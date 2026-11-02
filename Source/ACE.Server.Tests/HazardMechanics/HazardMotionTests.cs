using System;
using System.Linq;
using System.Numerics;

using ACE.Server.HazardMechanics;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.HazardMechanics
{
    [TestClass]
    public class HazardMotionTests
    {
        private const float Eps = 1e-4f;

        private static void AreClose(Vector3 expected, Vector3 actual, string what = null)
            => Assert.IsTrue(Vector3.Distance(expected, actual) < 1e-3f, $"{what}: expected {expected}, got {actual}");

        [TestMethod]
        public void Leg_PositionAt_InterpolatesAndClampsToEnds()
        {
            var leg = HazardMotion.Leg(new Vector3(0, 0, 0), new Vector3(10, 0, 0), 2.0, 4.0);

            AreClose(new Vector3(0, 0, 0), leg.PositionAt(0.0), "before t0");
            AreClose(new Vector3(0, 0, 0), leg.PositionAt(2.0), "at t0");
            AreClose(new Vector3(5, 0, 0), leg.PositionAt(4.0), "halfway");
            AreClose(new Vector3(10, 0, 0), leg.PositionAt(6.0), "at end");
            AreClose(new Vector3(10, 0, 0), leg.PositionAt(99.0), "after end");
            AreClose(new Vector3(2.5f, 0, 0), leg.Velocity, "velocity");
            Assert.AreEqual(6.0, leg.EndTime, 1e-9);
        }

        [TestMethod]
        public void Leg_ZeroDuration_IsAtEndWithZeroVelocity()
        {
            var leg = HazardMotion.Leg(Vector3.Zero, new Vector3(3, 4, 0), 1.0, 0.0);

            AreClose(new Vector3(3, 4, 0), leg.PositionAt(1.0));
            AreClose(Vector3.Zero, leg.Velocity);
        }

        [TestMethod]
        public void RollOmega_IsCrossOfUpAndVelocityOverRadius()
        {
            // Rolling east (+x) at 3 m/s, r = 1.5: omega = cross(z, x) * 3 / 1.5 = +y * 2.
            AreClose(new Vector3(0, 2, 0), HazardMotion.RollOmega(new Vector3(3, 0, 0), 1.5f));

            // Vertical velocity does not spin a rolling ball.
            AreClose(new Vector3(0, 2, 0), HazardMotion.RollOmega(new Vector3(3, 0, 7), 1.5f));

            AreClose(Vector3.Zero, HazardMotion.RollOmega(new Vector3(3, 0, 0), 0f));
        }

        [TestMethod]
        public void SubLegCount_CeilsTheTwoDLength()
        {
            Assert.AreEqual(1, HazardMotion.SubLegCount(Vector3.Zero, Vector3.Zero));
            Assert.AreEqual(1, HazardMotion.SubLegCount(Vector3.Zero, new Vector3(4, 0, 0)));
            Assert.AreEqual(3, HazardMotion.SubLegCount(Vector3.Zero, new Vector3(10, 0, 0)));

            // z does not count.
            Assert.AreEqual(1, HazardMotion.SubLegCount(Vector3.Zero, new Vector3(0, 0, 100)));

            Assert.AreEqual(HazardDefaults.MaxSubLegs, HazardMotion.SubLegCount(Vector3.Zero, new Vector3(10000, 0, 0)));
        }

        [TestMethod]
        public void Split_IntermediatePointsFollowTheHeightFunction_EndsKeepTheirZ()
        {
            Func<float, float, float> terrain = (x, y) => x * 0.5f;

            var legs = HazardMotion.Split(new Vector3(0, 0, 100), new Vector3(12, 0, 200), 10.0, 6.0, 3, terrain);

            Assert.AreEqual(3, legs.Count);
            AreClose(new Vector3(0, 0, 100), legs[0].Start);
            AreClose(new Vector3(4, 0, 2), legs[0].End, "sub-leg 1 end follows terrain");
            AreClose(new Vector3(8, 0, 4), legs[1].End, "sub-leg 2 end follows terrain");
            AreClose(new Vector3(12, 0, 200), legs[2].End, "true end keeps its z");

            Assert.AreEqual(10.0, legs[0].T0, 1e-9);
            Assert.AreEqual(12.0, legs[1].T0, 1e-9);
            Assert.AreEqual(14.0, legs[2].T0, 1e-9);
            Assert.AreEqual(16.0, legs[2].EndTime, 1e-9);
        }

        [TestMethod]
        public void Split_WithNoHeightFunction_IsTheStraightLine()
        {
            var legs = HazardMotion.Split(new Vector3(0, 0, 0), new Vector3(8, 0, 8), 0.0, 2.0, 2, null);

            AreClose(new Vector3(4, 0, 4), legs[0].End);
        }

        /// <summary>
        /// UNISON: three members with different lengths over different terrain get the SAME sub-leg count, so every
        /// member changes velocity at identical instants and all arrive at the same instant.
        /// </summary>
        [TestMethod]
        public void Unison_MembersWithDifferentTerrainPaths_GetIdenticalSubLegCountsAndTiming()
        {
            var members = new[]
            {
                (Start: new Vector3(0, 0, 0), End: new Vector3(2, 0, 0)),     // 2 m  -> needs 1
                (Start: new Vector3(0, 10, 0), End: new Vector3(18, 10, 0)),  // 18 m -> needs 5
                (Start: new Vector3(0, 20, 0), End: new Vector3(9, 20, 0)),   // 9 m  -> needs 3
            };

            Func<float, float, float>[] terrains =
            {
                (x, y) => 0f,
                (x, y) => MathF.Sin(x) * 3f,
                (x, y) => x * x * 0.1f,
            };

            var n = HazardMotion.UnisonSubLegCount(members.Select(m => (m.Start, m.End)));

            Assert.AreEqual(5, n, "the group shares the largest count any member needs");

            var split = members.Select((m, i) => HazardMotion.Split(m.Start, m.End, 3.0, 2.5, n, terrains[i])).ToList();

            foreach (var legs in split)
                Assert.AreEqual(n, legs.Count);

            for (var k = 0; k < n; k++)
            {
                Assert.AreEqual(split[0][k].T0, split[1][k].T0, 1e-12, $"sub-leg {k} start differs");
                Assert.AreEqual(split[0][k].T0, split[2][k].T0, 1e-12, $"sub-leg {k} start differs");
                Assert.AreEqual(split[0][k].Duration, split[1][k].Duration, 1e-12);
                Assert.AreEqual(split[0][k].Duration, split[2][k].Duration, 1e-12);
            }

            Assert.AreEqual(5.5, split[0].Last().EndTime, 1e-12);
            Assert.AreEqual(5.5, split[1].Last().EndTime, 1e-12);
            Assert.AreEqual(5.5, split[2].Last().EndTime, 1e-12);

            // and the terrain really differs between them (the test is not vacuous)
            Assert.AreNotEqual(split[1][0].End.Z, split[2][0].End.Z);
        }

        [TestMethod]
        public void Path_Advance_SendsEachNewSubLegVelocityOnce_ThenFinishes()
        {
            var legs = HazardMotion.Split(new Vector3(0, 0, 0), new Vector3(8, 0, 0), 0.0, 4.0, 2, null);
            var path = new HazardPath(legs);

            AreClose(new Vector3(2, 0, 0), path.SentVelocity, "first sub-leg velocity is the ideal one");
            Assert.AreEqual(HazardMotionStep.None, path.Advance(1.0));
            Assert.AreEqual(HazardMotionStep.NewVelocity, path.Advance(2.0));
            Assert.AreEqual(HazardMotionStep.None, path.Advance(2.1), "a boundary is reported once");
            Assert.AreEqual(HazardMotionStep.Finished, path.Advance(4.0));
            AreClose(new Vector3(8, 0, 0), path.End);
        }

        /// <summary>
        /// A boundary noticed late (the driver ticks every 100 ms) must not carry its error forward: the new
        /// velocity is aimed from where the client must be NOW at the sub-leg's end, arriving on time.
        /// </summary>
        [TestMethod]
        public void Path_LateBoundary_CorrectiveVelocityArrivesOnTime()
        {
            Func<float, float, float> terrain = (x, y) => x == 4f ? 2f : 0f; // a bump at the midpoint
            var legs = HazardMotion.Split(new Vector3(0, 0, 0), new Vector3(8, 0, 0), 0.0, 4.0, 2, terrain);
            var path = new HazardPath(legs);

            // client flies (2, 0, 1) m/s from the origin; boundary at t=2 noticed at t=2.3
            Assert.AreEqual(HazardMotionStep.NewVelocity, path.Advance(2.3));

            var predicted = new Vector3(4.6f, 0, 2.3f);
            AreClose(predicted, path.SentFrom, "prediction from what was sent");

            var arrival = path.SentFrom + path.SentVelocity * (float)(4.0 - 2.3);
            AreClose(new Vector3(8, 0, 0), arrival, "corrective velocity lands on the sub-leg end at its end time");

            // damage uses the IDEAL path, not the prediction
            AreClose(new Vector3(6, 0, 1), path.PositionAt(3.0));
        }

        [TestMethod]
        public void Path_RollRadius_GivesEachSubLegARollingOmega()
        {
            var legs = HazardMotion.Split(new Vector3(0, 0, 0), new Vector3(4, 0, 0), 0.0, 2.0, 1, null);
            var path = new HazardPath(legs, null, 0.8f);

            AreClose(new Vector3(0, 2f / 0.8f, 0), path.SentOmega);
        }

        [TestMethod]
        public void CorrectiveVelocity_FloorsTheTimeLeft()
        {
            var v = HazardMotion.CorrectiveVelocity(Vector3.Zero, new Vector3(1, 0, 0), 0.0);
            AreClose(new Vector3(20, 0, 0), v, "floored at 50 ms, not infinite");
        }
    }
}
