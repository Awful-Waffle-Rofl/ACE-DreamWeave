using System;
using System.Numerics;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.PuzzleGates;

namespace ACE.Server.Tests
{
    [TestClass]
    public class PuzzleBeamAimTests
    {
        private static void AssertAims(Vector3 d, PuzzleBeamAxis axis)
        {
            d = Vector3.Normalize(d);
            var q = PuzzleBeamAim.Aim(d, axis);
            var got = PuzzleBeamAim.BeamDirection(q, axis);
            Assert.IsTrue((got - d).Length() < 1e-4f, $"axis={axis} wanted {d} got {got}");
        }

        [TestMethod]
        public void Aim_StraightDown_ReproducesDirection()
        {
            AssertAims(new Vector3(0, 0, -1), PuzzleBeamAxis.Down);
            AssertAims(new Vector3(0, 0, -1), PuzzleBeamAxis.Up);
        }

        [TestMethod]
        public void Aim_StraightUp_ReproducesDirection()
        {
            AssertAims(new Vector3(0, 0, 1), PuzzleBeamAxis.Down);
            AssertAims(new Vector3(0, 0, 1), PuzzleBeamAxis.Up);
        }

        [TestMethod]
        public void Aim_EightyDegreeTilt_ReproducesDirection()
        {
            // 80 degrees from straight down, in several headings.
            for (var heading = 0; heading < 360; heading += 30)
            {
                var h = heading * Math.PI / 180.0;
                var t = 80.0 * Math.PI / 180.0;
                var d = new Vector3((float)(Math.Sin(t) * Math.Cos(h)), (float)(Math.Sin(t) * Math.Sin(h)), (float)-Math.Cos(t));
                AssertAims(d, PuzzleBeamAxis.Down);
                AssertAims(d, PuzzleBeamAxis.Up);
            }
        }

        [TestMethod]
        public void Aim_ManyDirections_ReproduceDirection()
        {
            var rng = new Random(1234);

            for (var i = 0; i < 500; i++)
            {
                var d = new Vector3((float)(rng.NextDouble() * 2 - 1), (float)(rng.NextDouble() * 2 - 1), (float)(rng.NextDouble() * 2 - 1));

                if (d.Length() < 0.05f)
                    continue;

                AssertAims(d, PuzzleBeamAxis.Down);
                AssertAims(d, PuzzleBeamAxis.Up);
            }
        }

        [TestMethod]
        public void Aim_AxisSwitchChangesTheOrientation()
        {
            var d = Vector3.Normalize(new Vector3(1, 2, -1));
            Assert.AreNotEqual(PuzzleBeamAim.Aim(d, PuzzleBeamAxis.Down), PuzzleBeamAim.Aim(d, PuzzleBeamAxis.Up));
        }

        [TestMethod]
        public void HostPosition_FarTarget_SitsBeamLengthTowardHub()
        {
            var target = new Vector3(1, 2, 0);
            var hub = new Vector3(1, 2, 20);
            var host = PuzzleBeamAim.HostPosition(target, hub, 5.0f);
            Assert.IsTrue((host - new Vector3(1, 2, 5)).Length() < 1e-4f);
        }

        [TestMethod]
        public void HostPosition_NearHub_ClampsToHub()
        {
            var target = new Vector3(0, 0, 0);
            var hub = new Vector3(0, 0, 3);
            var host = PuzzleBeamAim.HostPosition(target, hub, 5.0f);
            Assert.IsTrue((host - hub).Length() < 1e-4f);
        }
    }
}