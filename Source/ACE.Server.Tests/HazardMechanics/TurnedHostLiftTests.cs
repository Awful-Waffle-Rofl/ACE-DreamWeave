using System;
using System.Numerics;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.HazardMechanics;

namespace ACE.Server.Tests.HazardMechanics
{
    /// <summary>
    /// HazardGround.TurnedHostLift, the pure rule that raises a floor host turned 180 degrees about x (a rising beam) so
    /// its collision spheres stay above the floor. The dat-backed check that the rule actually spawns the Effect Host at
    /// the Black Book's floor points is ACE.Content.Tools.Tests TurnedFloorHostSpawnTests.
    /// </summary>
    [TestClass]
    public class TurnedHostLiftTests
    {
        // 0x02000176's sphere centre height, as `cells --spawncheck` printed it (origin z 0 -> sphere centre z 0.136)
        private const float EffectHostCentreZ = 0.136f;

        private static readonly Quaternion Turned = MemoryLayout.BeamOrientation(true);

        [TestMethod]
        public void Identity_never_lifts()
        {
            Assert.AreEqual(0f, HazardGround.TurnedHostLift(new[] { new Vector3(0, 0, EffectHostCentreZ) }, Quaternion.Identity));
            Assert.AreEqual(0f, HazardGround.TurnedHostLift(new[] { new Vector3(0, 0, EffectHostCentreZ) }, MemoryLayout.BeamOrientation(false)));
        }

        [TestMethod]
        public void A_turn_about_x_lifts_twice_the_centre_height()
        {
            Assert.AreEqual(2f * EffectHostCentreZ, HazardGround.TurnedHostLift(new[] { new Vector3(0, 0, EffectHostCentreZ) }, Turned), 1e-5f);

            // the turned centre then sits exactly where the unturned one did
            var lift = HazardGround.TurnedHostLift(new[] { new Vector3(0, 0, EffectHostCentreZ) }, Turned);
            Assert.AreEqual(EffectHostCentreZ, lift + Vector3.Transform(new Vector3(0, 0, EffectHostCentreZ), Turned).Z, 1e-5f);
        }

        [TestMethod]
        public void Off_axis_centres_lift_by_their_z_only()
        {
            // a turn about x maps (x, y, z) to (x, -y, -z): y and x do not change the lift
            Assert.AreEqual(1.0f, HazardGround.TurnedHostLift(new[] { new Vector3(3f, -2f, 0.5f) }, Turned), 1e-5f);
        }

        [TestMethod]
        public void Several_spheres_take_the_largest_lift()
        {
            var centres = new[] { new Vector3(0, 0, 0.2f), new Vector3(0, 0, 0.475f) };
            Assert.AreEqual(0.95f, HazardGround.TurnedHostLift(centres, Turned), 1e-5f);

            // and every turned centre then sits at or above its unturned height
            var lift = HazardGround.TurnedHostLift(centres, Turned);
            foreach (var c in centres)
                Assert.IsTrue(lift + Vector3.Transform(c, Turned).Z >= c.Z - 1e-5f, $"centre {c}");
        }

        [TestMethod]
        public void A_centre_below_the_origin_needs_no_lift()
        {
            Assert.AreEqual(0f, HazardGround.TurnedHostLift(new[] { new Vector3(0, 0, -0.3f) }, Turned), "turning moves it UP");
        }

        [TestMethod]
        public void A_yaw_keeps_z_and_never_lifts()
        {
            var yaw = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 1.234f);
            Assert.AreEqual(0f, HazardGround.TurnedHostLift(new[] { new Vector3(0.3f, 0.1f, 0.5f) }, yaw));
        }

        [TestMethod]
        public void No_centres_never_lifts()
        {
            Assert.AreEqual(0f, HazardGround.TurnedHostLift(null, Turned));
            Assert.AreEqual(0f, HazardGround.TurnedHostLift(Array.Empty<Vector3>(), Turned));
        }

        // ---- LiftedOrigin: HazardContext.Spawn's whole decision ----------------------------------------------------

        private static readonly Vector3 Floor = new Vector3(210f, -200f, 0f);

        private sealed class CountingCentres
        {
            public int Calls;

            public System.Collections.Generic.IReadOnlyList<Vector3> Get()
            {
                Calls++;
                return new[] { new Vector3(0, 0, EffectHostCentreZ) };
            }
        }

        [TestMethod]
        public void LiftedOrigin_turned_and_grounded_is_raised()
        {
            var c = new CountingCentres();
            var p = HazardGround.LiftedOrigin(true, false, c.Get, Turned, Floor);

            Assert.AreEqual(Floor.X, p.X);
            Assert.AreEqual(Floor.Y, p.Y);
            Assert.AreEqual(2f * EffectHostCentreZ, p.Z - Floor.Z, 1e-5f);
            Assert.AreEqual(1, c.Calls);
        }

        [TestMethod]
        public void LiftedOrigin_unturned_is_unchanged_and_skips_the_lookup()
        {
            var c = new CountingCentres();
            Assert.AreEqual(Floor, HazardGround.LiftedOrigin(true, false, c.Get, Quaternion.Identity, Floor));
            Assert.AreEqual(0, c.Calls, "an unturned spawn never pays the Setup lookup");
        }

        [TestMethod]
        public void LiftedOrigin_ceiling_host_not_grounded_is_unchanged()
        {
            var c = new CountingCentres();
            Assert.AreEqual(Floor, HazardGround.LiftedOrigin(false, false, c.Get, Turned, Floor), "a turned ceiling host (beam_flip) must not be pushed up into the ceiling");
            Assert.AreEqual(0, c.Calls);
        }

        [TestMethod]
        public void LiftedOrigin_moving_body_is_unchanged()
        {
            var c = new CountingCentres();
            Assert.AreEqual(Floor, HazardGround.LiftedOrigin(true, true, c.Get, Turned, Floor), "a moving body is never raised");
            Assert.AreEqual(0, c.Calls);
        }

        [TestMethod]
        public void LiftedOrigin_no_centres_is_unchanged()
        {
            Assert.AreEqual(Floor, HazardGround.LiftedOrigin(true, false, () => Array.Empty<Vector3>(), Turned, Floor));
            Assert.AreEqual(Floor, HazardGround.LiftedOrigin(true, false, null, Turned, Floor));
        }

        [TestMethod]
        public void A_non_finite_centre_is_ignored()
        {
            Assert.AreEqual(2f * EffectHostCentreZ, HazardGround.TurnedHostLift(new[] { new Vector3(0, 0, float.NaN), new Vector3(0, 0, EffectHostCentreZ) }, Turned), 1e-5f);
        }
    }
}
