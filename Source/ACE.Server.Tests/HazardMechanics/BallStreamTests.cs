using System;
using System.Linq;

using ACE.Server.HazardMechanics;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.HazardMechanics
{
    /// <summary>
    /// The random pattern's stream: ONE wave whose launches span "stream" seconds, with speed ramping launch by launch.
    /// Pure: no PropertyManager is touched.
    /// </summary>
    [TestClass]
    public class BallStreamTests
    {
        private const string Stream = "stream=60 count=20 speed=3 speed_end=9";

        private static HazardRegion Pinnacle() => HazardRegionPresets.Pinnacle(0);

        private static HazardArgs Merged(string args) => HazardArgs.Merge(HazardArgs.Parse(args), HazardArgs.Parse(HazardDefaults.Balls));

        private static BallParams P(string args, string pattern) => BallRules.Read(Merged(args), pattern);

        [TestMethod]
        public void Stream_IsOneWave_WindowEqualsStream()
        {
            var p = P(Stream + " waves=5 window=7", BallPatterns.Random);

            Assert.AreEqual(1, p.Waves, "a stream is one wave, whatever waves says");
            Assert.AreEqual(60.0, p.Window, "the launches span the stream, whatever window says");
            Assert.AreEqual(60.0, p.Stream);
            Assert.IsNull(BallRules.Validate(Pinnacle(), Merged(Stream), BallPatterns.Random), "the Grael P4 stream must start on the Pinnacle");

            // stream off: waves and window are read as before
            var off = P("waves=5 window=7", BallPatterns.Random);
            Assert.AreEqual(5, off.Waves);
            Assert.AreEqual(7.0, off.Window);

            // every launch falls inside the stream
            var wave = BallPatterns.Plan(Pinnacle(), BallPatterns.Random, p, new Random(3));
            Assert.IsTrue(wave.Routes.Count > 0);
            Assert.IsTrue(wave.Routes.All(r => r.Delay >= 0.0 && r.Delay < 60.0));
        }

        [TestMethod]
        public void Stream_PerLaunchSpeedRampsInLaunchOrder()
        {
            var p = P(Stream, BallPatterns.Random);
            var routes = BallPatterns.Plan(Pinnacle(), BallPatterns.Random, p, new Random(11)).Routes;
            var n = routes.Count;

            Assert.IsTrue(n >= 10, "enough launches to see a ramp: " + n);

            for (var k = 0; k < n; k++)
            {
                Assert.AreEqual((float)HazardRamp.At(3.0, 9.0, k + 1, n), routes[k].Speed, 1e-4f, "launch " + (k + 1));

                if (k > 0)
                {
                    Assert.IsTrue(routes[k].Delay >= routes[k - 1].Delay, "routes come in delay order");
                    Assert.IsTrue(routes[k].Speed >= routes[k - 1].Speed, "speed never falls along an ascending ramp");
                }
            }

            Assert.AreEqual(3f, routes[0].Speed, 1e-4f);
            Assert.AreEqual(9f, routes[n - 1].Speed, 1e-4f);
        }

        [TestMethod]
        public void Stream_DoesNotChangeWhereBallsRoll()
        {
            // the ramp adds a speed to each route and draws nothing: same seed, same lanes and delays as a stream with no ramp
            var ramped = BallPatterns.Plan(Pinnacle(), BallPatterns.Random, P(Stream, BallPatterns.Random), new Random(5)).Routes;
            var flat = BallPatterns.Plan(Pinnacle(), BallPatterns.Random, P("stream=60 count=20 speed=3", BallPatterns.Random), new Random(5)).Routes;

            Assert.AreEqual(flat.Count, ramped.Count);

            for (var i = 0; i < flat.Count; i++)
            {
                Assert.AreEqual(flat[i].From, ramped[i].From);
                Assert.AreEqual(flat[i].To, ramped[i].To);
                Assert.AreEqual(flat[i].Delay, ramped[i].Delay);
                Assert.AreEqual(0f, flat[i].Speed, "no speed_end, no per-route speed");
            }
        }

        [TestMethod]
        public void NoStream_SpeedEndRampsAcrossWavesNotLaunches()
        {
            var p = P("speed=3 speed_end=9 waves=4 count=6", BallPatterns.Random);
            var routes = BallPatterns.Plan(Pinnacle(), BallPatterns.Random, BallRules.ForWave(p, 2), new Random(2)).Routes;

            Assert.AreEqual(5.0f, BallRules.ForWave(p, 2).Speed, 1e-4f);
            Assert.IsTrue(routes.All(r => r.Speed == 0f), "without a stream every ball of a wave rolls at the wave's speed");
        }

        [TestMethod]
        public void Stream_LegAndFallbackUseRouteSpeed()
        {
            var from = new System.Numerics.Vector2(0f, 0f);
            var to = new System.Numerics.Vector2(18f, 0f);

            var own = new BallRoute(from, to, 4.0, "random-west", 9f);
            var plain = new BallRoute(from, to, 4.0, "random-west");

            // a route's own speed wins over the wave's; no own speed uses the wave's
            Assert.AreEqual(2.0, BallRules.LegSeconds(own, 3f), 1e-6);
            Assert.AreEqual(6.0, BallRules.LegSeconds(plain, 3f), 1e-6);
            Assert.AreEqual(BallRules.LegSeconds(plain, 9f), BallRules.LegSeconds(own, 3f), 1e-6);

            var withOwn = new BallWave();
            withOwn.Routes.Add(own);
            var withWave = new BallWave();
            withWave.Routes.Add(plain);

            Assert.AreEqual(BallRules.IdealSeconds(withWave, 9f, 2.0), BallRules.IdealSeconds(withOwn, 3f, 2.0), 1e-6, "ideal arrival: delay + lead + leg at the route's speed");
            Assert.AreEqual(4.0 + 2.0 + 2.0, BallRules.IdealSeconds(withOwn, 3f, 2.0), 1e-6);
            Assert.AreEqual(BallRules.FallbackSeconds(withWave, 9f, 2.0, 0.1), BallRules.FallbackSeconds(withOwn, 3f, 2.0, 0.1), 1e-6);
        }

        [TestMethod]
        public void Stream_RefusedOnNonRandomPattern()
        {
            foreach (var pattern in BallPatterns.All.Where(x => x != BallPatterns.Random))
                StringAssert.Contains(BallRules.Validate(Pinnacle(), Merged("stream=30"), pattern), "random pattern", pattern);

            Assert.IsNull(BallRules.Validate(Pinnacle(), Merged("stream=30 count=6"), BallPatterns.Random));

            // stream=0 is off, so any pattern accepts it
            Assert.IsNull(BallRules.Validate(Pinnacle(), Merged("stream=0"), BallPatterns.Row));
        }
    }
}
