using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using ACE.Server.HazardMechanics;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.HazardMechanics
{
    /// <summary>The rain's ball_radius arg: parse, fall time, ceiling check and the end of the fall. Pure, no world.</summary>
    [TestClass]
    public class RainBallRadiusTests
    {
        /// <summary>An open circle on a plane at z -36 with a ceiling at -11.7 (the Harbinger chamber's numbers).</summary>
        private static HazardRegion Chamber() => HazardRegion.Circle(
            "rain-radius-test", 0x1234, 0, true, 96f, 96f, 20f,
            HazardGroundMode.Plane, -36f, null, 0.5f, -11.7f, 2f, 6f);

        [TestMethod]
        public void BallRadius_DefaultsTo18_AndParses()
        {
            Assert.AreEqual(1.8, RainSettings.From(HazardArgs.Empty).BallRadius, 1e-6);
            Assert.AreEqual(0.4, RainSettings.From(HazardArgs.Parse("ball_radius=0.4")).BallRadius, 1e-6);
            Assert.AreEqual(0.1, RainSettings.From(HazardArgs.Parse("ball_radius=0.01")).BallRadius, 1e-6);
            Assert.AreEqual(5.0, RainSettings.From(HazardArgs.Parse("ball_radius=50")).BallRadius, 1e-6);
        }

        [TestMethod]
        public void FallSeconds_UsesTheRadius()
        {
            Assert.AreEqual((-14.0 - (-36.0 + 0.4)) / 12.0, RainRules.FallSeconds(-14.0, -36.0, 12.0, 0.4), 1e-9);
            Assert.AreEqual(RainRules.FallSeconds(-14.0, -36.0, 12.0, 1.8), RainRules.FallSeconds(-14.0, -36.0, 12.0), 1e-6);
        }

        [TestMethod]
        public void Validate_RefusesANonNumericRadius()
            => Assert.AreEqual("ball_radius='big' is not a number", RainRules.Validate(Chamber(), HazardArgs.Parse("ball_radius=big"), null));

        [TestMethod]
        public void Validate_CeilingCheckUsesTheRadius()
        {
            // The drop's top (drop_z + radius) must sit under the ceiling: -12.2 + 0.4 = -11.8 fits -11.7, -12.2 + 1.8 does not.
            Assert.IsNull(RainRules.Validate(Chamber(), HazardArgs.Parse("drop_z=-12.2 ball_radius=0.4 speed=12"), null));

            var line = RainRules.Validate(Chamber(), HazardArgs.Parse("drop_z=-12.2 speed=12"), null);
            Assert.IsNotNull(line);
            StringAssert.Contains(line, "ceiling");
        }

        [TestMethod]
        public void Director_EndsTheFallAtGroundPlusRadius()
        {
            var port = new Port();
            var s = RainSettings.From(HazardArgs.Parse("drop_z=-12.2 ball_radius=0.4 speed=12 duration=20"));
            var director = new RainDirector<Port.Obj>(Chamber(), s, port);
            director.Start(0.0);

            for (var now = 0.1; now <= 10.0 && port.Ends.Count == 0; now += 0.1)
            {
                port.Now = Math.Round(now, 6);
                director.Tick(port.Now);
            }

            Assert.IsTrue(port.Ends.Count > 0, "no ball fell");
            foreach (var z in port.Ends)
                Assert.AreEqual(-36.0f + 0.4f, z, 1e-4f);
        }

        private sealed class Port : IRainPort<Port.Obj>
        {
            public sealed class Obj { public bool Alive = true; }

            public double Now;
            public readonly List<float> Ends = new List<float>();

            public Random Rng { get; } = new Random(3);
            public IReadOnlyList<Vector3> PlayerPositions() => Array.Empty<Vector3>();
            public int? LiveCount => null;
            public bool Alive(Obj h) => h != null && h.Alive;
            public float GroundZ(float x, float y) => -36f;
            public bool HasCell(Vector3 point) => true;
            public Obj SpawnMarker(Vector3 point, double seconds) => new Obj();
            public Obj SpawnBall(Vector3 point) => new Obj();

            public bool Fall(Obj ball, Vector3 end, double seconds)
            {
                Ends.Add(end.Z);
                return true;
            }

            public Obj SpawnBurst(Vector3 point, double seconds) => new Obj();
            public int Strike(Obj source, Vector3 centre, float radius, double window) => 0;
            public void Destroy(Obj h) { if (h != null) h.Alive = false; }
            public void Say(string line) { }
            public void Log(string line) { }
            public void End(HazardOutcome outcome, string reason) { }
        }
    }
}
