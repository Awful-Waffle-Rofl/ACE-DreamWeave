using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.HazardMechanics;
using ACE.Server.Pinnacles.Grael;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.HazardMechanics
{
    /// <summary>
    /// The balls' opt-in CADENCE mode (wave_every / wave_every_end): launch times fixed by the args alone, waves that
    /// overlap and never wait on an earlier wave's balls, and the per-wave verdicts. Also the ring-alt pattern and the
    /// Grael P4 numbers built on both. Pure: no PropertyManager is touched.
    /// </summary>
    [TestClass]
    public class BallCadenceTests
    {
        private static HazardRegion Pinnacle() => HazardRegionPresets.Pinnacle(0);

        private static HazardArgs Merged(string args) => HazardArgs.Merge(HazardArgs.Parse(args), HazardArgs.Parse(HazardDefaults.Balls));

        private static BallParams P(string args, string pattern = BallPatterns.Row) => BallRules.Read(Merged(args), pattern);

        // ---- the args ------------------------------------------------------------------------------------------------

        [TestMethod]
        public void WaveEvery_IsOptIn_AndWithoutIt_NothingChanges()
        {
            var off = P("waves=6 wave_gap=2");

            Assert.IsFalse(off.Cadence, "no wave_every: the sequential mode");
            Assert.IsNull(off.WaveEvery);
            Assert.IsNull(BallRules.ForWave(off, 3).WaveEvery);
            Assert.AreEqual(0, BallRules.LaunchTimes(off).Count, "no launch schedule without wave_every");

            var on = P("waves=6 wave_every=3");

            Assert.IsTrue(on.Cadence);
            Assert.AreEqual(3.0, on.WaveEvery.Value, 1e-9);
        }

        [TestMethod]
        public void WaveEvery_IsClamped_AndRefusedWhenNotANumber()
        {
            Assert.AreEqual(HazardDefaults.BallsMinWaveEvery, P("wave_every=0.01").WaveEvery.Value, 1e-9);
            Assert.AreEqual(HazardDefaults.BallsMaxWaveEvery, P("wave_every=9999").WaveEvery.Value, 1e-9);
            Assert.AreEqual(HazardDefaults.BallsMinWaveEvery, P("wave_every=3 wave_every_end=0").WaveEveryEnd.Value, 1e-9);

            StringAssert.Contains(BallRules.Validate(Pinnacle(), Merged("wave_every=fast"), BallPatterns.Row), "wave_every");
            StringAssert.Contains(BallRules.Validate(Pinnacle(), Merged("wave_every=3 wave_every_end=soon"), BallPatterns.Row), "wave_every_end");
        }

        [TestMethod]
        public void LaunchTimes_RampLaunchToLaunch_OverTheWavesMinusOnePeriods()
        {
            var times = BallRules.LaunchTimes(P("waves=6 wave_every=3 wave_every_end=1.5"));
            var expected = new[] { 0.0, 3.0, 5.625, 7.875, 9.75, 11.25 };

            Assert.AreEqual(expected.Length, times.Count);

            for (var i = 0; i < expected.Length; i++)
                Assert.AreEqual(expected[i], times[i], 1e-9, "launch " + (i + 1));

            // a flat cadence
            CollectionAssert.AreEqual(new[] { 0.0, 2.0, 4.0 }, BallRules.LaunchTimes(P("waves=3 wave_every=2")).ToArray());

            // one wave: one launch at 0
            CollectionAssert.AreEqual(new[] { 0.0 }, BallRules.LaunchTimes(P("waves=1 wave_every=2")).ToArray());
        }

        // ---- BallCadence ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void Due_LaunchesOnTheClockAlone_NeverWaitingOnAnEarlierWavesBalls()
        {
            var cadence = new BallCadence(P("waves=4 wave_every=3"));

            CollectionAssert.AreEqual(new[] { 1 }, cadence.Due(0.0).ToArray(), "wave 1 at once");

            // wave 1 is open and NOTHING about its balls is ever resolved
            var first = cadence.Open(1, 5);
            first.OnSpawned(10);
            first.OnLaunched(10);

            Assert.AreEqual(0, cadence.Due(2.9).Count);
            CollectionAssert.AreEqual(new[] { 2 }, cadence.Due(3.0).ToArray(), "wave 2 launches on time with wave 1 still rolling");
            cadence.Open(2, 5);

            Assert.IsNull(cadence.Close(1), "wave 1 is still running");
            CollectionAssert.AreEqual(new[] { 1, 2 }, cadence.OpenWaves.ToArray(), "two waves in flight at once");

            // a slow tick makes two due at once, in order, each only once
            CollectionAssert.AreEqual(new[] { 3, 4 }, cadence.Due(50.0).ToArray());
            Assert.AreEqual(0, cadence.Due(99.0).Count);
            Assert.IsTrue(cadence.AllLaunched);
        }

        [TestMethod]
        public void Verdict_CompletedOnlyAfterTheLastLaunch_AndTheLastOpenWave()
        {
            var cadence = new BallCadence(P("waves=2 wave_every=3"));

            cadence.Due(0.0);
            var w1 = cadence.Open(1, 1);
            w1.OnSpawned(1);
            w1.OnLaunched(1);
            w1.OnLegEnd(1);

            var r1 = cadence.Close(1);

            Assert.IsTrue(r1.HasValue, "wave 1's ball is accounted for");
            Assert.IsNull(cadence.Verdict(1, r1.Value), "wave 2 has not launched yet: the run goes on");

            cadence.Due(3.0);
            var w2 = cadence.Open(2, 2);
            w2.OnSpawned(1);
            w2.OnLaunched(1);
            w2.OnLegEnd(1);

            Assert.IsNull(cadence.Close(2), "one of two balls still rolling");

            w2.OnSpawnFailed();
            var r2 = cadence.Close(2);

            Assert.IsTrue(r2.HasValue);

            var verdict = cadence.Verdict(2, r2.Value);

            Assert.AreEqual(HazardOutcome.Completed, verdict.Value.Outcome, "a partly lost wave carries on; the run completes");
            Assert.AreEqual(1, cadence.Lost);
            Assert.AreEqual(2, cadence.Spawned);
        }

        [TestMethod]
        public void Verdict_AWaveWhoseEveryBallWasProvenLost_Aborts_EvenWithOthersInFlight()
        {
            var cadence = new BallCadence(P("waves=3 wave_every=3"));

            cadence.Due(3.0);
            var w1 = cadence.Open(1, 2);
            var w2 = cadence.Open(2, 2);
            w1.OnSpawned(1);
            w1.OnLaunched(1);

            w2.OnSpawnFailed();
            w2.OnSpawnFailed();

            var r2 = cadence.Close(2);

            Assert.IsTrue(r2.HasValue);
            Assert.AreEqual(HazardOutcome.Aborted, cadence.Verdict(2, r2.Value).Value.Outcome);
        }

        [TestMethod]
        public void Verdict_ATimeoutWriteOffAloneNeverAborts()
        {
            var cadence = new BallCadence(P("waves=1 wave_every=3"));

            cadence.Due(0.0);
            var w = cadence.Open(1, 3);
            w.OnSpawned(1);
            w.ForceResolve();

            var r = cadence.Close(1);

            Assert.IsTrue(r.HasValue);
            Assert.AreEqual(HazardOutcome.Completed, cadence.Verdict(1, r.Value).Value.Outcome);
            Assert.AreEqual(3, cadence.WrittenOff);
        }

        // ---- ring-alt ------------------------------------------------------------------------------------------------

        [TestMethod]
        public void RingAlt_OddWavesRollOut_EvenWavesRollIn()
        {
            var region = Pinnacle();
            var p = P("count=6", BallPatterns.RingAlt);

            for (var wave = 1; wave <= 4; wave++)
            {
                var alt = BallPatterns.Plan(region, BallPatterns.RingAlt, p, new Random(9), wave);
                var reference = BallPatterns.Plan(region, wave % 2 == 1 ? BallPatterns.RingOut : BallPatterns.RingIn, p, new Random(9));

                Assert.AreEqual(reference.Routes.Count, alt.Routes.Count, "wave " + wave);

                for (var i = 0; i < alt.Routes.Count; i++)
                {
                    Assert.AreEqual(reference.Routes[i].From.X, alt.Routes[i].From.X, 1e-4, $"wave {wave} route {i}");
                    Assert.AreEqual(reference.Routes[i].From.Y, alt.Routes[i].From.Y, 1e-4, $"wave {wave} route {i}");
                    Assert.AreEqual(reference.Routes[i].To.X, alt.Routes[i].To.X, 1e-4, $"wave {wave} route {i}");
                    Assert.AreEqual(reference.Routes[i].To.Y, alt.Routes[i].To.Y, 1e-4, $"wave {wave} route {i}");
                }

                if (wave % 2 == 0)
                    Assert.IsTrue(alt.Routes.All(r => Math.Abs(r.To.X - region.CenterX) < 1e-4 && Math.Abs(r.To.Y - region.CenterY) < 1e-4), "an even wave converges on the centre");
                else
                    Assert.IsTrue(alt.Routes.All(r => Math.Abs(r.From.X - region.CenterX) < 1e-4 && Math.Abs(r.From.Y - region.CenterY) < 1e-4), "an odd wave starts at the centre");
            }
        }

        [TestMethod]
        public void RingAlt_IsARegisteredPattern_ThatValidatesLikeTheRings()
        {
            Assert.IsTrue(BallPatterns.All.Contains(BallPatterns.RingAlt));
            Assert.AreEqual(BallPatterns.RingOut, BallPatterns.All[0], "the default pattern is unchanged");
            Assert.IsTrue(BallPatterns.IsRing(BallPatterns.RingAlt));
            Assert.IsNull(BallRules.Validate(Pinnacle(), Merged("count=6"), BallPatterns.RingAlt));
        }

        // ---- max_in_flight ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void MaxInFlight_HoldsADueWave_UntilAnEarlierOneFinishes_ThenLaunchesIt()
        {
            var cadence = new BallCadence(P("waves=4 wave_every=1 max_in_flight=2"));

            CollectionAssert.AreEqual(new[] { 1 }, cadence.Due(0.0).ToArray());
            cadence.Open(1, 1).OnSpawned(1);
            CollectionAssert.AreEqual(new[] { 2 }, cadence.Due(1.0).ToArray());
            cadence.Open(2, 1).OnSpawned(1);

            // wave 3's time has come, but two are in flight: held, however late it gets
            Assert.AreEqual(0, cadence.Due(2.0).Count);
            Assert.IsTrue(cadence.Held);
            Assert.AreEqual(0, cadence.Due(9.0).Count, "still held while both roll");

            // wave 1 finishes: wave 3 launches at once
            cadence.ClockOf(1).OnLegEnd(1);
            Assert.IsTrue(cadence.Close(1).HasValue);
            CollectionAssert.AreEqual(new[] { 3 }, cadence.Due(9.1).ToArray(), "one slot freed, one wave launched");
            cadence.Open(3, 1);
            Assert.AreEqual(2, cadence.InFlight);

            // a capped schedule SLIDES: wave 4 is due one period after wave 3 really launched, not in a burst behind it
            Assert.AreEqual(10.1, cadence.DueAt(4), 1e-9);
            Assert.AreEqual(0, cadence.Due(10.0).Count);
            Assert.IsFalse(cadence.Held, "not yet due, so not held");
            Assert.AreEqual(0, cadence.Due(10.1).Count, "due now, but two are in flight");
            Assert.IsTrue(cadence.Held);

            // an UNcapped schedule never slides: its times are fixed from the start
            var fixedClock = new BallCadence(P("waves=4 wave_every=1"));
            Assert.AreEqual(3.0, fixedClock.DueAt(4), 1e-9);
        }

        [TestMethod]
        public void MaxInFlight_RampsAndRounds_PerWave()
        {
            var p = P("waves=6 wave_every=3 max_in_flight=2 max_in_flight_end=3");
            var cadence = new BallCadence(p);

            CollectionAssert.AreEqual(new int?[] { 2, 2, 2, 3, 3, 3 }, Enumerable.Range(1, 6).Select(cadence.CapFor).ToArray(), "2 + (3 - 2) x (k - 1) / 5, rounded half away from zero");
            Assert.IsNull(new BallCadence(P("waves=6 wave_every=3")).CapFor(3), "uncapped without the arg");
        }

        [TestMethod]
        public void MaxInFlight_WithoutWaveEvery_IsRefused_AndWithoutTheArg_NothingIsHeld()
        {
            StringAssert.Contains(BallRules.Validate(Pinnacle(), Merged("max_in_flight=2"), BallPatterns.Row), "only works with wave_every");
            StringAssert.Contains(BallRules.Validate(Pinnacle(), Merged("max_in_flight=two wave_every=3"), BallPatterns.Row), "max_in_flight");

            var cadence = new BallCadence(P("waves=5 wave_every=1"));

            for (var i = 0; i < 5; i++)
                foreach (var w in cadence.Due(i))
                    cadence.Open(w, 3);

            Assert.AreEqual(5, cadence.InFlight, "no cap: every wave launches on its time with all the others still open");
            Assert.IsFalse(cadence.Held);
        }

        /// <summary>
        /// The Grael row series on the cadence clock with REAL wave lifetimes: each wave is planned on the Pinnacle and
        /// lives its ideal time (its fair lead plus its slowest lane at its ramped speed, BallRules.IdealSeconds), then
        /// closes. Returns, per wave, its launch time and the waves in flight at its launch (itself included).
        /// </summary>
        private static List<(int Wave, double At, int InFlight, int Cap)> SimulateGraelRows(out double lastClose)
        {
            var region = Pinnacle();
            var p = BallRules.Read(HazardArgs.Merge(GraelSettings.Defaults.P4RowArgs(), HazardArgs.Parse(HazardDefaults.Balls)), BallPatterns.Row);
            var cadence = new BallCadence(p);
            var rng = new Random(5);
            var closesAt = new Dictionary<int, double>();
            var log = new List<(int, double, int, int)>();
            lastClose = 0.0;

            for (var t = 0.0; t < 600.0 && !(cadence.AllLaunched && cadence.InFlight == 0); t += 0.1)
            {
                foreach (var w in closesAt.Where(kv => kv.Value <= t + 1e-9).Select(kv => kv.Key).ToList())
                {
                    var clock = cadence.ClockOf(w);

                    foreach (var id in clock.Unresolved())
                        clock.OnLegEnd(id);

                    Assert.IsTrue(cadence.Close(w).HasValue);
                    closesAt.Remove(w);
                    lastClose = t;
                }

                foreach (var w in cadence.Due(t))
                {
                    var wp = BallRules.ForWave(p, w);
                    var plan = BallPatterns.Plan(region, BallPatterns.Row, wp, rng);
                    var lead = BallRules.LeadFor(region, BallPatterns.Row, wp, null);
                    var clock = cadence.Open(w, plan.Routes.Count);

                    for (var i = 0; i < plan.Routes.Count; i++)
                    {
                        clock.OnSpawned(i);
                        clock.OnLaunched(i);
                    }

                    closesAt[w] = t + BallRules.IdealSeconds(plan, wp.Speed, lead);
                    log.Add((w, t, cadence.InFlight, cadence.CapFor(w).Value));
                }
            }

            return log;
        }

        [TestMethod]
        public void GraelRows_TwoInFlightEarly_ThreeLate_NeverOverTheCap()
        {
            var log = SimulateGraelRows(out var lastClose);
            var line = string.Join(" | ", log.Select(l => FormattableString.Invariant($"w{l.Wave} t={l.At:0.0} inflight={l.InFlight}/{l.Cap}")));

            Console.WriteLine("GRAEL ROWS: " + line + FormattableString.Invariant($" | last close t={lastClose:0.0}"));

            Assert.AreEqual(6, log.Count, line);
            Assert.IsTrue(log.All(l => l.InFlight <= l.Cap), "never over the cap: " + line);
            Assert.IsTrue(log.Where(l => l.Cap == 2).Any(l => l.InFlight == 2), "early, 2 rows really do overlap: " + line);
            Assert.IsTrue(log.Where(l => l.Cap == 3).Any(l => l.InFlight == 3), "late, 3 rows are in flight together: " + line);

            // no burst: launches are never closer than the cadence's tightest period (the capped schedule slides)
            for (var i = 1; i < log.Count; i++)
                Assert.IsTrue(log[i].At - log[i - 1].At >= 1.5 - 1e-6, $"waves {log[i - 1].Wave} and {log[i].Wave} launched {log[i].At - log[i - 1].At:0.0} s apart: " + line);

            // early the cap spaces the rows wider than the 3 s cadence alone
            var earlyGap = log[2].At - log[1].At;

            Assert.IsTrue(earlyGap > 3.0 + 1.0, $"the early gap ({earlyGap:0.0} s) is visibly wider than the old 3 s: " + line);
        }

        // ---- Grael P4 ------------------------------------------------------------------------------------------------

        [TestMethod]
        public void GraelP4_LaunchesEveryThreeSecondsRampingToOneAndAHalf()
        {
            var s = GraelSettings.Defaults;
            var balls = BallRules.Read(HazardArgs.Merge(s.P4BallArgs(), HazardArgs.Parse(HazardDefaults.Balls)), BallPatterns.Fan);
            var times = BallRules.LaunchTimes(balls);

            Assert.IsTrue(balls.Cadence);
            Assert.IsFalse(balls.Marker, "no gap pillars in P4");
            Assert.AreEqual(6, times.Count);

            var periods = times.Zip(times.Skip(1), (a, b) => b - a).ToList();

            Assert.AreEqual(3.0, periods.First(), 1e-9);
            Assert.AreEqual(1.5, periods.Last(), 1e-9);
            Assert.IsTrue(periods.Zip(periods.Skip(1), (a, b) => b < a).All(x => x), "the cadence tightens wave by wave");

            // the ONE ring series: twice the waves (12 ring waves in all, as the old out and in series ran), same 3.0 -> 1.5
            var rings = BallRules.Read(HazardArgs.Merge(s.P4RingArgs(), HazardArgs.Parse(HazardDefaults.Balls)), BallPatterns.RingAlt);
            var ringTimes = BallRules.LaunchTimes(rings);

            Assert.AreEqual(12, rings.Waves);
            Assert.AreEqual(12, ringTimes.Count);
            Assert.AreEqual(3.0, ringTimes[1] - ringTimes[0], 1e-9);
            Assert.AreEqual(1.5, ringTimes[11] - ringTimes[10], 1e-9);
        }
    }
}
