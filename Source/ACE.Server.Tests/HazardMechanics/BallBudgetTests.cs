using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.HazardMechanics;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.HazardMechanics
{
    /// <summary>
    /// HAZARDS-PLAN A2, balls: the opt-in ball budget (args max_balls, replaced by the run's live count). A due wave
    /// launches when nothing is in flight or in-flight + its planned size fits; a held wave keeps its plan, so the random
    /// numbers are drawn in the same order as without a budget. With neither max_balls nor a live count the Due
    /// sequence, the planning order and the opens are exactly the cadence's own. Pure: BallCadence driven by a script.
    /// </summary>
    [TestClass]
    public class BallBudgetTests
    {
        private static HazardArgs Merged(string args) => HazardArgs.Merge(HazardArgs.Parse(args), HazardArgs.Parse(HazardDefaults.Balls));

        private static BallParams P(string args, string pattern = BallPatterns.Row) => BallRules.Read(Merged(args), pattern);

        private sealed class W
        {
            public int No;
            public int Size;
            public int Draw;
        }

        // ---- the pure rules ------------------------------------------------------------------------------------------

        [TestMethod]
        public void Budget_LiveCountWins_ClampsToItsRange_AndIsNullWhenBothAreNull()
        {
            Assert.IsNull(BallBudget.For(null, null));
            Assert.AreEqual(8, BallBudget.For(null, 8));
            Assert.AreEqual(5, BallBudget.For(5, 8), "the live count replaces max_balls");
            Assert.AreEqual(5, BallBudget.For(5, null));
            Assert.AreEqual(BallBudget.Min, BallBudget.For(0, null));
            Assert.AreEqual(BallBudget.Min, BallBudget.For(-4, 8));
            Assert.AreEqual(BallBudget.Max, BallBudget.For(100000, null));
        }

        [TestMethod]
        public void Admits_NothingInFlightAlways_ElseOnlyWhenTheWaveFits()
        {
            Assert.IsTrue(BallBudget.Admits(null, 1000, 1000), "no budget admits everything");
            Assert.IsTrue(BallBudget.Admits(4, 0, 20), "an empty flight admits a wave bigger than the budget");
            Assert.IsTrue(BallBudget.Admits(10, 6, 4), "6 + 4 = 10 fits");
            Assert.IsFalse(BallBudget.Admits(10, 6, 5), "6 + 5 does not");
            Assert.IsTrue(BallBudget.Admits(1, 7, 0), "an empty plan is never held (it ends the run instead)");
        }

        // ---- the arg ------------------------------------------------------------------------------------------------

        [TestMethod]
        public void MaxBalls_IsOptIn_Clamped_AndNeedsWaveEvery()
        {
            Assert.IsNull(P("waves=4 wave_every=2").MaxBalls, "absent: no budget");
            Assert.IsNull(BallRules.ForWave(P("waves=4 wave_every=2"), 2).MaxBalls);
            Assert.AreEqual(12, P("waves=4 wave_every=2 max_balls=12").MaxBalls);
            Assert.AreEqual(12, BallRules.ForWave(P("waves=4 wave_every=2 max_balls=12"), 3).MaxBalls, "carried to every wave's numbers");
            Assert.AreEqual(BallBudget.Min, P("waves=4 wave_every=2 max_balls=0").MaxBalls);
            Assert.AreEqual(BallBudget.Max, P("waves=4 wave_every=2 max_balls=99999").MaxBalls);
        }

        [TestMethod]
        public void MaxBalls_WithoutWaveEvery_IsRefused_AndNotANumberIsRefused()
        {
            var region = HazardRegionPresets.Pinnacle(0);

            StringAssert.Contains(BallRules.Validate(region, Merged("waves=3 max_balls=8"), BallPatterns.Row), "max_balls only works with wave_every");
            StringAssert.Contains(BallRules.Validate(region, Merged("waves=3 wave_every=3 max_balls=lots"), BallPatterns.Row), "max_balls");
            Assert.IsNull(BallRules.Validate(region, Merged("waves=3 wave_every=3 max_balls=8"), BallPatterns.Row));
        }

        // ---- no budget: the cadence is exactly as it was --------------------------------------------------------------

        private static (List<string> Calls, List<(double T, int Wave)> Opens) Reference(string args, double lifetime)
        {
            // the pre-A2 loop: Due, then plan and open each returned wave in order
            var p = P(args);
            var cadence = new BallCadence(p, BallPatterns.Row);
            var calls = new List<string>();
            var opens = new List<(double, int)>();
            var closeAt = new Dictionary<int, double>();

            for (var t = 0.0; t <= 80.0; t += 0.5)
            {
                foreach (var n in cadence.Due(t))
                {
                    calls.Add("plan " + n);
                    cadence.Open(n, 4);
                    opens.Add((t, n));
                    closeAt[n] = t + lifetime;
                }

                Settle(cadence, closeAt, t);
            }

            return (calls, opens);
        }

        private static void Settle(BallCadence cadence, Dictionary<int, double> closeAt, double t)
        {
            foreach (var n in cadence.OpenWaves.ToList())
            {
                if (closeAt.TryGetValue(n, out var at) && t >= at)
                {
                    var clock = cadence.ClockOf(n);

                    for (var id = 0; id < 4; id++)
                    {
                        var key = n * 100 + id;
                        clock.OnSpawned(key);
                        clock.OnLaunched(key);
                        clock.OnLegEnd(key);
                    }

                    cadence.Close(n);
                }
            }
        }

        [TestMethod]
        public void NoBudget_LaunchIsTheOldDueLoop_WaveForWave_AcrossCapsAndRamps()
        {
            foreach (var args in new[]
            {
                "waves=6 wave_every=2",
                "waves=6 wave_every=3 wave_every_end=1.5",
                "waves=8 wave_every=1 max_in_flight=2",
                "waves=8 wave_every=1 max_in_flight=1 max_in_flight_end=3",
            })
            {
                var expected = Reference(args, lifetime: 3.0);

                var p = P(args);
                var cadence = new BallCadence(p, BallPatterns.Row);
                var calls = new List<string>();
                var opens = new List<(double, int)>();
                var closeAt = new Dictionary<int, double>();

                for (var t = 0.0; t <= 80.0; t += 0.5)
                {
                    var now = t;

                    cadence.Launch(t,
                        n => { calls.Add("plan " + n); return new W { No = n, Size = 4 }; },
                        w => w.Size,
                        () => BallBudget.For(null, p.MaxBalls),
                        (n, w) =>
                        {
                            cadence.Open(n, w.Size);
                            opens.Add((now, n));
                            closeAt[n] = now + 3.0;
                            return true;
                        });

                    Settle(cadence, closeAt, t);
                    Assert.AreEqual(0, cadence.HeldWave, args + ": nothing is ever held without a budget");
                }

                CollectionAssert.AreEqual(expected.Calls, calls, args + ": the planning order");
                CollectionAssert.AreEqual(expected.Opens, opens, args + ": the open sequence");
                Assert.IsTrue(opens.Count >= 6, args + ": the waves actually ran");
            }
        }

        // ---- a budget -----------------------------------------------------------------------------------------------

        private static (List<string> Log, List<(double T, int Wave)> Opens, List<int> Draws) RunBudget(string args, Func<int?> budget, double lifetime, double upTo = 80.0)
        {
            var p = P(args);
            var cadence = new BallCadence(p, BallPatterns.Row);
            var rng = new Random(77);
            var log = new List<string>();
            var opens = new List<(double, int)>();
            var draws = new List<int>();
            var closeAt = new Dictionary<int, double>();

            for (var t = 0.0; t <= upTo; t += 0.5)
            {
                var now = t;

                cadence.Launch(t,
                    n =>
                    {
                        log.Add("plan " + n);
                        return new W { No = n, Size = 4, Draw = rng.Next(1000) };
                    },
                    w => w.Size,
                    budget,
                    (n, w) =>
                    {
                        cadence.Open(n, w.Size);
                        opens.Add((now, n));
                        draws.Add(w.Draw);
                        closeAt[n] = now + lifetime;
                        return true;
                    });

                Settle(cadence, closeAt, t);
            }

            return (log, opens, draws);
        }

        [TestMethod]
        public void AHeldWave_KeepsItsPlan_AndNothingPlansBehindIt()
        {
            // waves of 4 balls every 1 s, each in flight 3 s; a budget of 6 holds the wave that would make it 8
            var run = RunBudget("waves=5 wave_every=1", () => 6, lifetime: 3.0);

            Assert.AreEqual(1, run.Log.Count(l => l == "plan 2"), "wave 2 is planned once however long it is held");
            Assert.AreEqual(1, run.Log.Count(l => l == "plan 3"), "and so is every other wave");

            CollectionAssert.AreEqual(new[] { 1, 2, 3, 4, 5 }, run.Opens.Select(o => o.Wave).ToArray(), "waves launch in order, none skipped");
            Assert.IsTrue(run.Opens[1].T >= run.Opens[0].T + 3.0 - 1e-9, "wave 2 waited for wave 1's four balls: " + string.Join(",", run.Opens));

            // the plans are made in wave order, one wave at a time
            CollectionAssert.AreEqual(new[] { "plan 1", "plan 2", "plan 3", "plan 4", "plan 5" }, run.Log);
        }

        [TestMethod]
        public void TheRandomNumbersAreDrawnInTheSameOrder_WithAndWithoutABudget()
        {
            var free = RunBudget("waves=5 wave_every=1", () => null, lifetime: 3.0);
            var held = RunBudget("waves=5 wave_every=1", () => 6, lifetime: 3.0);

            Assert.AreNotEqual(free.Opens[1].T, held.Opens[1].T, "the budget really held a wave");
            CollectionAssert.AreEqual(free.Draws, held.Draws, "wave k got the same random draw either way: a held wave kept its plan");
        }

        [TestMethod]
        public void AWaveThatFits_LaunchesOnTime_AndTheBudgetIsReadPerDecision()
        {
            // 4 + 4 = 8 fits a budget of 8: nothing is held
            var fits = RunBudget("waves=4 wave_every=1", () => 8, lifetime: 3.0);
            Assert.AreEqual(1.0, fits.Opens[1].T, 1e-9, "wave 2 on time: " + string.Join(",", fits.Opens));

            // a live count that rises lets a held wave go the moment it rises
            var budget = 4;
            var p = P("waves=3 wave_every=1");
            var cadence = new BallCadence(p, BallPatterns.Row);
            var opened = new List<int>();

            Func<double, int[]> step = t =>
            {
                cadence.Launch(t, n => new W { No = n, Size = 4 }, w => w.Size, () => budget, (n, w) => { cadence.Open(n, w.Size); opened.Add(n); return true; });
                return opened.ToArray();
            };

            CollectionAssert.AreEqual(new[] { 1 }, step(0.0));
            CollectionAssert.AreEqual(new[] { 1 }, step(1.0), "4 in flight + 4 > 4: wave 2 is held");
            Assert.AreEqual(2, cadence.HeldWave);
            Assert.AreEqual((4, 4, 4), cadence.HeldFor);
            CollectionAssert.AreEqual(new[] { 1 }, step(2.0), "still held, and wave 3 is not handed out behind it");

            budget = 8;
            CollectionAssert.AreEqual(new[] { 1, 2 }, step(2.5), "the budget rose: wave 2 launches at once");
            Assert.AreEqual(3, cadence.HeldWave, "and wave 3, due since t=2, is now the held one (8 + 4 > 8)");
        }

        [TestMethod]
        public void AnEmptyFlight_AlwaysLaunches_EvenAWaveBiggerThanTheBudget()
        {
            var run = RunBudget("waves=3 wave_every=1", () => 2, lifetime: 2.0);

            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, run.Opens.Select(o => o.Wave).ToArray(), "a budget under one wave still runs the waves, one at a time");

            for (var i = 1; i < run.Opens.Count; i++)
                Assert.IsTrue(run.Opens[i].T >= run.Opens[i - 1].T + 2.0 - 1e-9, "never two at once: " + string.Join(",", run.Opens));
        }

        [TestMethod]
        public void TheRunIsNotCompleted_WhileAWaveIsHeld()
        {
            var p = P("waves=2 wave_every=1");
            var cadence = new BallCadence(p, BallPatterns.Row);

            Func<int?> budget = () => 4;
            Func<double, bool> tick = t =>
            {
                cadence.Launch(t, n => new W { No = n, Size = 4 }, w => w.Size, budget, (n, w) => { cadence.Open(n, w.Size); return true; });
                return cadence.AllLaunched;
            };

            Assert.IsFalse(tick(0.0));
            Assert.IsFalse(tick(1.0), "wave 2's time came but it is held, so the last wave has not launched");

            var clock = cadence.ClockOf(1);

            for (var id = 0; id < 4; id++)
            {
                clock.OnSpawned(id);
                clock.OnLaunched(id);
                clock.OnLegEnd(id);
            }

            var result = cadence.Close(1);
            Assert.IsNotNull(result);
            Assert.IsNull(cadence.Verdict(1, result.Value), "no Completed verdict with a wave still waiting to launch");

            Assert.IsTrue(tick(1.5), "wave 1's balls are done: wave 2 launches and all are launched");
        }

        [TestMethod]
        public void BallsInFlight_CountsPlannedBallsNotYetAccountedFor()
        {
            var cadence = new BallCadence(P("waves=3 wave_every=1"), BallPatterns.Row);
            cadence.Due(0.0);
            var clock = cadence.Open(1, 5);

            Assert.AreEqual(5, cadence.BallsInFlight, "planned counts before any ball has spawned");

            clock.OnSpawnFailed();
            Assert.AreEqual(4, cadence.BallsInFlight);

            clock.OnSpawned(1);
            clock.OnLaunched(1);
            clock.OnLegEnd(1);
            Assert.AreEqual(3, cadence.BallsInFlight);

            clock.OnSpawned(2);
            clock.OnDied(2);
            Assert.AreEqual(2, cadence.BallsInFlight);
        }
    }
}