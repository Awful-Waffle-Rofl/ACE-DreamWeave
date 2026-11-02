using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using ACE.Server.HazardMechanics;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.HazardMechanics
{
    /// <summary>The rain run, driven through a recording fake of the world on a fake 100 ms clock.</summary>
    [TestClass]
    public class RainDirectorTests
    {
        private const double Dt = 0.1;

        private sealed class Obj
        {
            public int Id;
            public string Kind;
            public bool Alive = true;
            public Vector3 At;
            public Vector3? FallEnd;
            public double FallSeconds;
        }

        private sealed class FakePort : IRainPort<Obj>
        {
            private int next;

            public FakePort(int seed) { Rng = new Random(seed); }

            public Random Rng { get; }

            public int? Live;

            public List<Vector3> Players = new List<Vector3>();

            public int PlayerCalls;

            public IReadOnlyList<Vector3> PlayerPositions()
            {
                PlayerCalls++;
                return Players;
            }

            public int LiveReads;

            public int? LiveCount
            {
                get
                {
                    LiveReads++;
                    return Live;
                }
            }

            public double Now;
            public float Ground;
            public bool CellOk = true;
            public bool MarkerOk = true;
            public bool BallOk = true;
            public int Hits = 1;

            public readonly List<Obj> All = new List<Obj>();
            public readonly List<string> Trace = new List<string>();
            public readonly List<(double Now, Obj Source, Vector3 Centre, float Radius, double Window)> Strikes = new List<(double, Obj, Vector3, float, double)>();
            public readonly List<(double Now, string Kind)> Calls = new List<(double, string)>();

            public HazardOutcome? Ended;
            public string EndedReason;

            public bool Alive(Obj h) => h != null && h.Alive;

            public float GroundZ(float x, float y) => Ground;

            public bool HasCell(Vector3 point) => CellOk;

            private Obj Make(string kind, Vector3 at)
            {
                var o = new Obj { Id = ++next, Kind = kind, At = at };
                All.Add(o);
                Trace.Add($"{Now:0.0} spawn {kind}#{o.Id} ({at.X:0.00},{at.Y:0.00},{at.Z:0.00})");
                Calls.Add((Now, "spawn-" + kind));
                return o;
            }

            public Obj SpawnMarker(Vector3 point, double seconds) => MarkerOk ? Make("marker", point) : null;

            public Obj SpawnBall(Vector3 point) => BallOk ? Make("ball", point) : null;

            public bool Fall(Obj ball, Vector3 end, double seconds)
            {
                if (!ball.Alive)
                    return false;

                ball.FallEnd = end;
                ball.FallSeconds = seconds;
                Trace.Add($"{Now:0.0} fall #{ball.Id} -> ({end.X:0.00},{end.Y:0.00},{end.Z:0.00}) in {seconds:0.000}");
                Calls.Add((Now, "fall"));
                return true;
            }

            public Obj SpawnBurst(Vector3 point, double seconds) => Make("burst", point);

            public int Strike(Obj source, Vector3 centre, float radius, double window)
            {
                Strikes.Add((Now, source, centre, radius, window));
                Trace.Add($"{Now:0.0} strike #{source.Id} ({centre.X:0.00},{centre.Y:0.00},{centre.Z:0.00}) r={radius:0.##}");
                Calls.Add((Now, "strike"));
                return Hits;
            }

            public void Destroy(Obj h)
            {
                if (h == null || !h.Alive)
                    return;

                h.Alive = false;
                Trace.Add($"{Now:0.0} destroy {h.Kind}#{h.Id}");
                Calls.Add((Now, "destroy-" + h.Kind));
            }

            public void Say(string line) => Trace.Add($"{Now:0.0} say {line}");

            public void Log(string line) => Trace.Add($"{Now:0.0} log {line}");

            public void End(HazardOutcome outcome, string reason)
            {
                Ended = outcome;
                EndedReason = reason;
                Trace.Add($"{Now:0.0} end {outcome} {reason}");
            }
        }

        private static (RainDirector<Obj> Director, FakePort Port) Make(string args, int seed = 1, HazardRegion region = null)
        {
            var port = new FakePort(seed);
            var director = new RainDirector<Obj>(region ?? RainRulesTests.Open(), RainSettings.From(HazardArgs.Parse(args)), port);
            director.Start(0.0);
            return (director, port);
        }

        /// <summary>Ticks on from the port's current time until the run ends or `until`; returns the max balls under way seen after any tick.</summary>
        private static int Drive(RainDirector<Obj> d, FakePort p, double until, Action<double> beforeTick = null)
        {
            var max = 0;

            for (var now = p.Now + Dt; now <= until + 1e-9 && p.Ended == null; now += Dt)
            {
                p.Now = Math.Round(now, 6);
                beforeTick?.Invoke(p.Now);
                d.Tick(p.Now);
                max = Math.Max(max, d.InFlight);
            }

            return max;
        }

        // ---- cadence ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void Cadence_NeverExceedsTheCap_AndKeepsLaunching()
        {
            var (d, p) = Make("max_falling=4 duration=60");

            var max = Drive(d, p, 59.0);

            Assert.IsTrue(max <= 4, $"in flight reached {max}");
            Assert.IsTrue(max >= 3, $"the cap should be approached, saw {max}");
            Assert.IsTrue(d.Launched > 20, $"launched {d.Launched}");
            Assert.IsTrue(d.Impacts > 15, $"impacts {d.Impacts}");
        }

        [TestMethod]
        public void Cadence_IsAStreamNotVolleys_LaunchesAreSpreadOverTime()
        {
            var (d, p) = Make("max_falling=6 duration=40");
            Drive(d, p, 39.0);

            var times = p.Calls.Where(c => c.Kind == "spawn-ball").Select(c => c.Now).ToList();
            var gaps = times.Zip(times.Skip(1), (a, b) => Math.Round(b - a, 3)).ToList();

            Assert.IsTrue(times.Count > 15);
            Assert.IsTrue(gaps.Distinct().Count() > 5, "random gaps");
            Assert.IsTrue(gaps.Count(g => g < 0.05) < gaps.Count / 4, "balls are not dropped in synchronised batches");
        }

        [TestMethod]
        public void LiveCount_IsReadOnlyAtLaunchDecisions_AndTakesEffectAtTheNextOne()
        {
            var (d, p) = Make("max_falling=2 duration=120");

            // phase 1: live count null -> max_falling 2
            p.Live = null;
            var first = Drive(d, p, 30.0);
            Assert.IsTrue(first <= 2, $"cap 2, saw {first}");
            Assert.AreEqual(2, d.LastCap);

            // phase 2: the world event raises the count between ticks: it is picked up at a later decision
            p.Live = 12;
            var readsBefore = p.LiveReads;
            var peak = 0;

            for (var now = 30.0 + Dt; now <= 70.0; now += Dt)
            {
                p.Now = Math.Round(now, 6);
                d.Tick(p.Now);
                peak = Math.Max(peak, d.InFlight);
            }

            Assert.AreEqual(12, d.LastCap);
            Assert.IsTrue(peak > 4, $"the raised count should be used, peak {peak}");
            Assert.IsTrue(peak <= 12, $"peak {peak}");

            // phase 3: it falls to 1: no new launch while 1 or more are under way, so the count drains to <= 1 launches at a time
            p.Live = 1;
            var late = 0;

            for (var now = 70.0 + Dt; now <= 119.0; now += Dt)
            {
                p.Now = Math.Round(now, 6);
                d.Tick(p.Now);

                if (now > 90.0)
                    late = Math.Max(late, d.InFlight);
            }

            Assert.AreEqual(1, d.LastCap);
            Assert.AreEqual(1, late, "settled to a single ball at a time");

            // the count is read once per decision, never per tick: far fewer reads than ticks
            Assert.IsTrue(p.LiveReads > readsBefore);
            Assert.IsTrue(p.LiveReads < 1190 / 2, $"reads {p.LiveReads}");
        }

        [TestMethod]
        public void LiveCount_IsNotReadAfterTheDuration()
        {
            var (d, p) = Make("max_falling=3 duration=10");

            Drive(d, p, 10.0);
            var reads = p.LiveReads;

            Drive(d, p, 30.0);

            Assert.AreEqual(reads, p.LiveReads, "no launch decision after the duration");
            Assert.AreEqual(HazardOutcome.Completed, p.Ended);
        }

        // ---- one drop ------------------------------------------------------------------------------------------------

        [TestMethod]
        public void OneDrop_MarkerThenBall_FallsAfterTheLead_AndBurstsOnImpact()
        {
            // one ball at a time, a long gap
            var (d, p) = Make("max_falling=1 duration=1 speed=10 lead=0.6 burst_radius=4 drop_z=31.8");
            Drive(d, p, 8.0);

            var ball = p.All.First(o => o.Kind == "ball");
            var marker = p.All.First(o => o.Kind == "marker");

            // the marker is up before the ball, at the impact point on the ground; the ball is at drop height straight above it
            Assert.IsTrue(p.All.IndexOf(marker) < p.All.IndexOf(ball));
            Assert.AreEqual(31.8f, ball.At.Z, 1e-4f);
            Assert.AreEqual(marker.At.X, ball.At.X);
            Assert.AreEqual(marker.At.Y, ball.At.Y);
            Assert.AreEqual(0f, marker.At.Z);

            // one straight leg to the ground plus the ball radius, at speed: 30 m at 10 m/s
            Assert.IsNotNull(ball.FallEnd);
            Assert.AreEqual(ball.At.X, ball.FallEnd.Value.X);
            Assert.AreEqual(ball.At.Y, ball.FallEnd.Value.Y);
            Assert.AreEqual(RainRules.BallRadius, ball.FallEnd.Value.Z, 1e-4f);
            Assert.AreEqual(3.0, ball.FallSeconds, 1e-6);
            Assert.AreEqual(1, p.Calls.Count(c => c.Kind == "fall"));

            // exactly one strike, at the impact point, with burst_radius, once per ball
            Assert.AreEqual(1, p.Strikes.Count);
            Assert.AreSame(ball, p.Strikes[0].Source);
            Assert.AreEqual(marker.At, p.Strikes[0].Centre);
            Assert.AreEqual(4f, p.Strikes[0].Radius);
            Assert.AreEqual(double.PositiveInfinity, p.Strikes[0].Window);

            // the fall starts after the lead, the strike lands when the leg ends
            var fallAt = p.Calls.First(c => c.Kind == "fall").Now;
            var spawnAt = p.Calls.First(c => c.Kind == "spawn-ball").Now;
            Assert.IsTrue(fallAt >= spawnAt + 0.6 - 1e-9, "after the lead");
            Assert.IsTrue(fallAt <= spawnAt + 0.6 + Dt + 1e-9, "no later than a tick after it");
            Assert.AreEqual(fallAt + 3.0, p.Strikes[0].Now, Dt + 1e-9);

            // burst and vanish: a burst at the impact point, the ball and the marker destroyed
            var burst = p.All.First(o => o.Kind == "burst");
            Assert.AreEqual(marker.At, burst.At);
            Assert.IsFalse(ball.Alive);
            Assert.IsFalse(marker.Alive);
            Assert.AreEqual(1, d.Impacts);
            Assert.AreEqual(1, d.Hits);
        }

        [TestMethod]
        public void TheMarkerStandsForTheWholeFall_AndGoesWithTheBall()
        {
            var (d, p) = Make("max_falling=1 duration=1 drop_z=31.8");

            // up to just before impact the marker is alive; after the burst it is gone
            Drive(d, p, 3.0);
            var marker = p.All.First(o => o.Kind == "marker");
            var ball = p.All.First(o => o.Kind == "ball");

            Assert.IsTrue(marker.Alive && ball.Alive, "still falling at t=3");

            Drive(d, p, 8.0);
            Assert.IsFalse(marker.Alive);
            Assert.IsFalse(ball.Alive);
        }

        [TestMethod]
        public void TheTopUpForAFairWarningGoesToAnEarlierMarker_NotToAHoveringBall()
        {
            // 11 m drop: fall 0.92 s, floor 2.0 s, lead 0.6: the warning needs 1.08 s, so the marker stands 0.48 s before the ball appears
            var (d, p) = Make("max_falling=1 duration=1 drop_z=11 lead=0.6");
            Drive(d, p, 6.0);

            var markerAt = p.Calls.First(c => c.Kind == "spawn-marker").Now;
            var spawnAt = p.Calls.First(c => c.Kind == "spawn-ball").Now;
            var fallAt = p.Calls.First(c => c.Kind == "fall").Now;
            var strikeAt = p.Strikes.Single().Now;

            Assert.IsTrue(spawnAt >= markerAt + 0.48 - 1e-9, $"ball at {spawnAt}, marker {markerAt}");
            Assert.IsTrue(fallAt >= spawnAt + 0.6 - 1e-9 && fallAt <= spawnAt + 0.6 + Dt + 1e-9, $"the ball hangs only its lead: spawned {spawnAt}, fell {fallAt}");
            Assert.IsTrue(strikeAt - markerAt >= 2.0 - 1e-9, $"warning {strikeAt - markerAt}");
            Assert.IsTrue(p.Trace.Any(l => l.Contains("lead raised")));
        }

        [TestMethod]
        public void WithNoTopUp_TheBallAppearsWithItsMarker()
        {
            var (d, p) = Make("max_falling=1 duration=1 drop_z=31.8 lead=0.2");
            Drive(d, p, 6.0);

            Assert.AreEqual(p.Calls.First(c => c.Kind == "spawn-marker").Now, p.Calls.First(c => c.Kind == "spawn-ball").Now);

            var fallAt = p.Calls.First(c => c.Kind == "fall").Now;
            var spawnAt = p.Calls.First(c => c.Kind == "spawn-ball").Now;
            Assert.IsTrue(fallAt <= spawnAt + 0.2 + Dt + 1e-9, "a 0.2 s lead is a spawn-in, not a hover");
        }

        [TestMethod]
        public void ABallThatCannotBeSpawnedLate_TakesItsMarkerWithIt()
        {
            var (d, p) = Make("max_falling=1 duration=1 drop_z=11 lead=0.6");
            Drive(d, p, 0.2); // marker up, ball still pending
            p.BallOk = false;
            Drive(d, p, 3.0);

            Assert.AreEqual(0, p.All.Count(o => o.Kind == "ball"));
            Assert.IsTrue(p.All.Where(o => o.Kind == "marker").All(o => !o.Alive));
            Assert.AreEqual(0, p.Strikes.Count);
        }
        [TestMethod]
        public void ADeadBall_NeverHits_AndItsMarkerGoes()
        {
            var (d, p) = Make("max_falling=1 duration=1 drop_z=31.8");

            Drive(d, p, 1.5);
            var ball = p.All.First(o => o.Kind == "ball");
            ball.Alive = false; // /reload-landblock

            Drive(d, p, 8.0);

            Assert.AreEqual(0, p.Strikes.Count);
            Assert.AreEqual(1, d.Lost);
            Assert.IsFalse(p.All.First(o => o.Kind == "marker").Alive);
            Assert.AreEqual(HazardOutcome.Completed, p.Ended);
        }

        [TestMethod]
        public void NoMarkerNoBall_AndNoBallLeavesNoMarker()
        {
            var (d, p) = Make("max_falling=3 duration=5");
            p.MarkerOk = false;
            Drive(d, p, 3.0);
            Assert.AreEqual(0, p.All.Count(o => o.Kind == "ball"), "a ball with no marker would be an unannounced hit");

            var (d2, p2) = Make("max_falling=3 duration=5");
            p2.BallOk = false;
            Drive(d2, p2, 3.0);
            Assert.IsTrue(p2.All.Count(o => o.Kind == "marker") > 0);
            Assert.IsTrue(p2.All.Where(o => o.Kind == "marker").All(o => !o.Alive), "a refused ball takes its marker with it");
        }

        // ---- the end ---------------------------------------------------------------------------------------------------

        [TestMethod]
        public void Completes_AfterTheDuration_OnceTheBallsInTheAirHaveLanded()
        {
            var (d, p) = Make("max_falling=4 duration=20");

            Drive(d, p, 19.9);
            Assert.IsNull(p.Ended, "still raining");

            Drive(d, p, 60.0);

            Assert.AreEqual(HazardOutcome.Completed, p.Ended);
            Assert.AreEqual(0, d.InFlight);
            Assert.AreEqual(d.Launched, d.Impacts, "every launched ball landed");

            var lastLaunch = p.Calls.Where(c => c.Kind == "spawn-ball").Max(c => c.Now);
            Assert.IsTrue(lastLaunch < 20.0 + 1e-9, "nothing launched after the duration");
        }

        [TestMethod]
        public void Aborts_WhenNothingCanEverBePlaced()
        {
            var (d, p) = Make("max_falling=3 duration=60");
            p.CellOk = false;

            Drive(d, p, 120.0);

            Assert.AreEqual(HazardOutcome.Aborted, p.Ended);
            Assert.AreEqual(0, d.Launched);
        }

        [TestMethod]
        public void ADropNotAboveTheGround_IsSkippedAndNotedOnce()
        {
            var (d, p) = Make("max_falling=3 duration=60 drop_z=1");
            Drive(d, p, 120.0);

            Assert.AreEqual(0, d.Launched);
            Assert.AreEqual(1, p.Trace.Count(l => l.Contains("is not above the ground")));
        }

        // ---- aiming --------------------------------------------------------------------------------------------------

        private static double Dist2D(Vector3 a, Vector3 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

        [TestMethod]
        public void Aim_ShareOne_EveryImpactIsWithinTheSpreadOfThePlayer()
        {
            var player = new Vector3(96f, 96f, 0f);
            var (d, p) = Make("max_falling=6 duration=60 target_share=1 target_spread=5", 3);
            p.Players.Add(player);
            Drive(d, p, 120.0);

            Assert.IsTrue(p.Strikes.Count > 20);
            Assert.AreEqual(d.Launched, d.Aimed);

            foreach (var s in p.Strikes)
                Assert.IsTrue(Dist2D(s.Centre, player) <= 5.0 + 1e-3, $"impact {s.Centre} is {Dist2D(s.Centre, player):0.00} m from the player");
        }

        [TestMethod]
        public void Aim_SpreadZero_ImpactIsThePlayersPoint()
        {
            var player = new Vector3(100f, 90f, 0f);
            var (d, p) = Make("max_falling=4 duration=30 target_share=1 target_spread=0", 4);
            p.Players.Add(player);
            Drive(d, p, 90.0);

            Assert.IsTrue(p.Strikes.Count > 5);

            foreach (var s in p.Strikes)
            {
                Assert.AreEqual(player.X, s.Centre.X, 1e-4f);
                Assert.AreEqual(player.Y, s.Centre.Y, 1e-4f);
            }
        }

        [TestMethod]
        public void Aim_ShareOne_NoPlayers_StillLaunchesUniformly()
        {
            var region = RainRulesTests.Open();
            var (d, p) = Make("max_falling=6 duration=30 target_share=1", 5, region);
            Drive(d, p, 90.0);

            Assert.IsTrue(d.Launched > 10);
            Assert.AreEqual(0, d.Aimed);
            Assert.AreEqual(0, d.Impacts - d.Launched);
            Assert.IsTrue(p.Calls.Count(c => c.Kind == "spawn-ball") > 10);
            Assert.AreEqual(HazardOutcome.Completed, p.Ended);

            foreach (var s in p.Strikes)
                Assert.IsTrue(region.IsPlaceable(s.Centre.X, s.Centre.Y));
        }

        [TestMethod]
        public void Aim_PlayerWhereNoDiscPointIsAcceptable_FallsBackToPlaceablePoints()
        {
            // the player stands outside the circle (radius 40 about 96,96) with a 3 m spread: every disc point is rejected
            var region = RainRulesTests.Open();
            var (d, p) = Make("max_falling=6 duration=30 target_share=1 target_spread=3", 6, region);
            p.Players.Add(new Vector3(160f, 96f, 0f));
            Drive(d, p, 90.0);

            Assert.IsTrue(p.PlayerCalls > 0);
            Assert.IsTrue(d.Launched > 10);
            Assert.AreEqual(0, d.Aimed);

            foreach (var s in p.Strikes)
            {
                Assert.IsTrue(region.IsPlaceable(s.Centre.X, s.Centre.Y));
                Assert.IsTrue(region.DistanceToEdge(s.Centre.X, s.Centre.Y) >= 2f - 1e-3f);
            }
        }

        [TestMethod]
        public void Aim_ShareZero_NeverAsksForPlayers_AndKeepsTheUniformStream()
        {
            var (d, p) = Make("max_falling=5 duration=30 target_share=0", 11);
            p.Players.Add(new Vector3(96f, 96f, 0f));
            Drive(d, p, 90.0);

            Assert.AreEqual(0, p.PlayerCalls);
            Assert.AreEqual(0, d.Aimed);

            // golden: the first 8 impact centres captured from the pre-aiming code (origin/master 6242ab18c) for this seed and args
            const string golden = "59.5944,92.5513;69.7901,69.7409;82.3973,87.6754;71.5697,71.3856;128.0078,94.9056;78.2787,99.7404;126.0344,118.2404;87.3147,96.2646";
            var expected = golden.Split(';').Select(e => e.Split(',')).ToList();

            Assert.IsTrue(p.Strikes.Count >= expected.Count);

            for (var i = 0; i < expected.Count; i++)
            {
                Assert.AreEqual(double.Parse(expected[i][0], System.Globalization.CultureInfo.InvariantCulture), p.Strikes[i].Centre.X, 1e-3, $"impact {i} x");
                Assert.AreEqual(double.Parse(expected[i][1], System.Globalization.CultureInfo.InvariantCulture), p.Strikes[i].Centre.Y, 1e-3, $"impact {i} y");
            }
        }

        [TestMethod]
        public void Aim_DefaultShare_AimsSomeButNotAll()
        {
            var (d, p) = Make("max_falling=6 duration=60", 8);
            p.Players.Add(new Vector3(96f, 96f, 0f));
            Drive(d, p, 120.0);

            Assert.IsTrue(d.Aimed > 0 && d.Aimed < d.Launched, $"aimed {d.Aimed} of {d.Launched}");
        }

        // ---- determinism --------------------------------------------------------------------------------------------------

        [TestMethod]
        public void SameSeed_SameTrace_DifferentSeed_DifferentTrace()
        {
            string RunTrace(int seed)
            {
                var (d, p) = Make("max_falling=5 duration=30", seed);
                Drive(d, p, 90.0);
                return string.Join("\n", p.Trace);
            }

            var a = RunTrace(11);
            var b = RunTrace(11);
            var c = RunTrace(12);

            Assert.AreEqual(a, b);
            Assert.AreNotEqual(a, c);
            Assert.IsTrue(a.Length > 1000);
        }

        [TestMethod]
        public void EveryImpactPoint_IsInsideTheRegionAndTheEdgeInset()
        {
            var region = RainRulesTests.Open();
            var (d, p) = Make("max_falling=6 duration=60 edge_inset=3", 5, region);
            Drive(d, p, 120.0);

            Assert.IsTrue(p.Strikes.Count > 20);

            foreach (var s in p.Strikes)
            {
                Assert.IsTrue(region.Contains(s.Centre.X, s.Centre.Y));
                Assert.IsTrue(region.DistanceToEdge(s.Centre.X, s.Centre.Y) >= 3f - 1e-3f);
            }
        }
    }
}
