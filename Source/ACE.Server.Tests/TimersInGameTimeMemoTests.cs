using System;
using System.Diagnostics;

using ACE.Common;
using ACE.Server.Entity;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Timers.CurrentInGameTimeIsDay delegates to DerethDateTime.IsDayAtTicks, which memoizes on the
    /// Derethian quarter-hour quantum (round(ticks / hourTicks * 4, ToEven)) rather than on PortalYearTicks
    /// assignment. That distinction matters: WorldManager.UpdateWorld() (which assigns PortalYearTicks) runs
    /// about 863 times per second on prod, while the quantum only advances about once every 119 seconds of
    /// portal-year time - a much coarser gate. An earlier version of this fix memoized per-assignment instead
    /// and would have recomputed the ~640,000-iteration DerethDateTime constructor 863 times/second, a ~37x
    /// regression versus the bug it was fixing. PortalYearTicks has an internal setter, reachable here via
    /// the ACE.Server -> ACE.Server.Tests InternalsVisibleTo grant (ACE.Server.csproj:15).
    /// </summary>
    [TestClass]
    public class TimersInGameTimeMemoTests
    {
        private static readonly double OriginalPortalYearTicks = Timers.PortalYearTicks;

        // hourTicks = dayTicks(7620) / hoursInADay(16), mirrors DerethDateTime's private constant.
        private const double HourTicks = 476.25;

        [TestCleanup]
        public void Cleanup()
        {
            // Restore so this test class does not leak a mutated PortalYearTicks into tests that run after it
            // in the same process.
            Timers.PortalYearTicks = OriginalPortalYearTicks;
        }

        [TestMethod]
        public void CurrentInGameTimeIsDay_matches_DerethDateTime_across_a_sweep_including_boundaries()
        {
            var sawDay = false;
            var sawNight = false;

            // A present-day tick value (~304,600,000 as of 2026-09-26), small values near the epoch, and a
            // dense sweep of hourTicks-scaled values around both day/night boundary hours (Dawnsong and
            // Warmtide_and_Half), to exercise SetDateTimeFromTicks' round_decimals == 25 correction at the
            // exact boundary.
            foreach (var ticks in SweepValues())
            {
                Timers.PortalYearTicks = ticks;

                var expected = new DerethDateTime(ticks).IsDay;
                var actual = Timers.CurrentInGameTimeIsDay;

                Assert.AreEqual(expected, actual, $"Mismatch at PortalYearTicks={ticks}");

                if (expected)
                    sawDay = true;
                else
                    sawNight = true;
            }

            // A sweep that never observes both values could pass while comparing nothing interesting.
            Assert.IsTrue(sawDay, "Sweep never produced a day value - test would not discriminate.");
            Assert.IsTrue(sawNight, "Sweep never produced a night value - test would not discriminate.");
        }

        private static System.Collections.Generic.IEnumerable<double> SweepValues()
        {
            yield return 0;
            yield return 1;
            yield return HourTicks;
            yield return 304_600_000; // present-day (2026) in-game tick value

            // Sweep every quarter-hour across several days near the epoch, to cross both the Dawnsong and
            // Warmtide_and_Half boundary hours (and their +/- quarter-hour neighbors) repeatedly.
            for (var hourQuarter = 0; hourQuarter < 16 * 4 * 5; hourQuarter++)
                yield return hourQuarter * (HourTicks / 4.0);

            // Same sweep anchored at the present-day value, so the boundary crossings are also exercised far
            // from the epoch, where the SetDateTimeFromTicks loop is actually expensive.
            for (var hourQuarter = 0; hourQuarter < 16 * 4 * 5; hourQuarter++)
                yield return 304_600_000 + hourQuarter * (HourTicks / 4.0);
        }

        [TestMethod]
        public void CurrentInGameTimeIsDay_quantum_gate_is_exact_not_approximate_across_transitions()
        {
            // Proves the memo never returns a stale answer across a quantum transition: sample far finer than
            // one quantum (hourTicks/4 ticks per quantum; sampled every hourTicks/16 here, i.e. 4 samples per
            // quantum) across several full Derethian days, and compare the memoized answer against a freshly
            // constructed DerethDateTime at every single sample - not just at the transition points.
            const int daysToSweep = 3;
            var totalTicksToSweep = DerethDateTime_DayTicksForTest * daysToSweep;
            var step = HourTicks / 16.0;

            var transitionsToDay = 0;
            var transitionsToNight = 0;
            bool? previous = null;

            for (var ticks = 0.0; ticks < totalTicksToSweep; ticks += step)
            {
                Timers.PortalYearTicks = ticks;

                var expected = new DerethDateTime(ticks).IsDay;
                var actual = Timers.CurrentInGameTimeIsDay;

                Assert.AreEqual(expected, actual, $"Stale memo detected at PortalYearTicks={ticks}");

                if (previous.HasValue && previous.Value != expected)
                {
                    if (expected)
                        transitionsToDay++;
                    else
                        transitionsToNight++;
                }

                previous = expected;
            }

            // A sweep that never crosses in both directions would not prove the gate handles a transition
            // correctly, only that it handles whichever single direction it happened to observe.
            Assert.IsTrue(transitionsToDay > 0, "Sweep never transitioned night -> day.");
            Assert.IsTrue(transitionsToNight > 0, "Sweep never transitioned day -> night.");
        }

        // dayTicks = 7620, mirrors DerethDateTime's private constant (hourTicks * hoursInADay = 476.25 * 16).
        private const double DerethDateTime_DayTicksForTest = 7620;

        [TestMethod]
        public void CurrentInGameTimeIsDay_is_memoized_not_recomputed_per_read()
        {
            // Pick a known day value and a known night value near the present-day tick range.
            const double presentDay = 304_600_000;

            Timers.PortalYearTicks = presentDay;
            var dayIsh = new DerethDateTime(presentDay).IsDay;

            // Find a nearby night-side value by stepping forward in whole-hour increments until IsDay flips.
            var nightTicks = presentDay;
            for (var i = 0; i < 16; i++)
            {
                nightTicks += HourTicks;
                if (new DerethDateTime(nightTicks).IsDay != dayIsh)
                    break;
            }

            Timers.PortalYearTicks = presentDay;
            Assert.AreEqual(dayIsh, Timers.CurrentInGameTimeIsDay);

            // 100,000 reads with PortalYearTicks held constant. A non-memoized implementation (constructing a
            // DerethDateTime per read) costs about 2.782 ms/call at this tick magnitude, i.e. ~278 seconds for
            // 100,000 reads - a memoized read is a field access plus an Interlocked.Read, on the order of
            // nanoseconds. The 50 ms budget below is about 5000x looser than the memoized cost and about
            // 5500x tighter than the unmemoized cost, so this is not a timing-flaky assertion: do not "fix" it
            // by loosening the threshold.
            var sw = Stopwatch.StartNew();
            bool lastRead = false;
            for (var i = 0; i < 100_000; i++)
                lastRead = Timers.CurrentInGameTimeIsDay;
            sw.Stop();

            Assert.AreEqual(dayIsh, lastRead);
            Assert.IsTrue(sw.Elapsed.TotalMilliseconds < 50,
                $"100,000 reads took {sw.Elapsed.TotalMilliseconds} ms - expected under 50 ms for a memoized value.");

            // And prove the cache isn't a hardcoded constant: moving PortalYearTicks to the night-side value
            // must change the cached flag.
            Timers.PortalYearTicks = nightTicks;
            Assert.AreNotEqual(dayIsh, Timers.CurrentInGameTimeIsDay);
        }

        [TestMethod]
        public void CurrentInGameTimeIsDay_survives_realistic_per_tick_advances_without_the_full_recompute_cost()
        {
            // This is the regression guard for the defect an earlier version of this fix shipped: memoizing
            // per PortalYearTicks ASSIGNMENT rather than per quantum. WorldManager.UpdateWorld() (which
            // assigns PortalYearTicks) was measured on prod's own SLOW_TICK iter counter at 863 iterations per
            // second (iteration 34,587 at 18:29:30 to 7,709,854 at 20:57:42 over 8,892 s), each iteration
            // advancing PortalYearTicks by roughly that iteration's real elapsed seconds. So a realistic
            // per-iteration delta is about 1/863 =~ 0.00116 seconds, within the 0.001-0.0005 s range this test
            // uses. Against a per-assignment memo (reconstructing DerethDateTime on every PortalYearTicks
            // write) this loop would cost about 2.782 ms * 100,000 =~ 278 seconds. Against the quantum-gated
            // memo it only reconstructs when the quarter-hour quantum (every ~119 s of portal-year time)
            // actually changes, so it finishes in well under 50 ms. Do not "fix" this by loosening the
            // threshold, and do not simplify the memo back to per-assignment.
            const double perIterationDelta = 0.00116;

            Timers.PortalYearTicks = 304_600_000;

            var sw = Stopwatch.StartNew();
            for (var i = 0; i < 100_000; i++)
            {
                Timers.PortalYearTicks += perIterationDelta;
                _ = Timers.CurrentInGameTimeIsDay;
            }
            sw.Stop();

            Assert.IsTrue(sw.Elapsed.TotalMilliseconds < 50,
                $"100,000 realistic per-tick advances took {sw.Elapsed.TotalMilliseconds} ms - expected under 50 ms " +
                "for a memo gated on the quarter-hour quantum rather than on every PortalYearTicks assignment.");
        }
    }
}
