using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.HazardMechanics;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.HazardMechanics
{
    /// <summary>
    /// HAZARDS-PLAN A2 follow-ups: element-coloured markers (wisps mark_script, balls marker_script) and the count
    /// ramp at low counts (BallRules.CapToLiveCount: under a live count no wave is planned bigger than the budget).
    /// Pure: planners and BallCadence only.
    /// </summary>
    [TestClass]
    public class BallLiveCountCapTests
    {
        private static HazardArgs Merged(string args) => HazardArgs.Merge(HazardArgs.Parse(args), HazardArgs.Parse(HazardDefaults.Balls));

        private static BallParams P(string args, string pattern) => BallRules.Read(Merged(args), pattern);

        /// <summary>A circular arena of radius 60 (the size the event pool is judged at).</summary>
        private static HazardRegion Arena() => HazardRegion.Circle("arena", 0x0069, 0, false, 96f, 96f, 60f, HazardGroundMode.Plane, -47.75f, null, 2f, null, 2f, 6f);

        private static int Size(string pattern, string args, int? liveCount)
        {
            var p = BallRules.CapToLiveCount(P(args, pattern), liveCount);
            return BallPatterns.Plan(Arena(), pattern, p, new Random(5), 1).Routes.Count;
        }

        // ---- element-coloured markers ---------------------------------------------------------------------------------

        [TestMethod]
        public void MarkerScript_DefaultsToThePillar_ParsesDecimalAndHex_AndIsRefusedWhenNotAScript()
        {
            Assert.AreEqual(HazardDefaults.PillarScript, P("", BallPatterns.Row).MarkerScript);
            Assert.AreEqual(0x33001029u, P("marker_script=0x33001029", BallPatterns.Row).MarkerScript);
            Assert.AreEqual(12345u, P("marker_script=12345", BallPatterns.Row).MarkerScript);
            Assert.AreEqual(0x33001029u, BallRules.ForWave(P("marker_script=0x33001029", BallPatterns.Row), 3).MarkerScript, "carried to every wave");

            StringAssert.Contains(BallRules.Validate(HazardRegionPresets.Pinnacle(0), Merged("marker_script=flame"), BallPatterns.Row), "marker_script='flame' is not a script id");
            Assert.IsNull(BallRules.Validate(HazardRegionPresets.Pinnacle(0), Merged("marker_script=0x33001029"), BallPatterns.Row));
        }

        [TestMethod]
        public void MarkScript_ForWisps_DefaultsToTheBeamMark_ParsesHex_AndIsRefused()
        {
            Assert.AreEqual(HazardDefaults.WispDestMarkScript, WispSettings.From(HazardArgs.Empty).MarkScript);
            Assert.AreEqual(0x33001029u, WispSettings.From(HazardArgs.Parse("mark_script=0x33001029")).MarkScript);
            Assert.AreEqual(0x33001029u, WispSettings.From(HazardArgs.Parse("mark_script=0x33001029 move_end=3")).ForCycle(2).MarkScript, "a ramped cycle keeps it");

            StringAssert.Contains(WispRules.Validate(HazardRegionPresets.Pinnacle(0), HazardArgs.Parse("mark_script=flame"), "scatter"), "mark_script='flame' is not a script id");
            Assert.IsNull(WispRules.Validate(HazardRegionPresets.Pinnacle(0), HazardArgs.Parse("mark_script=0x33001029"), "scatter"));
        }

        // ---- the count ramp --------------------------------------------------------------------------------------------

        [TestMethod]
        public void NullLiveCount_ReturnsTheSameParams()
        {
            var p = P("count=10", BallPatterns.Random);

            Assert.AreSame(p, BallRules.CapToLiveCount(p, null), "no live count: not even a copy");
            Assert.AreSame(p, BallRules.CapToLiveCount(p, 10), "a budget at or above the count changes nothing");
            Assert.AreSame(p, BallRules.CapToLiveCount(p, 99));
        }

        [TestMethod]
        public void CountParameterisedPatterns_ShrinkToTheBudget_AndEveryOtherPatternKeepsItsSize()
        {
            foreach (var (pattern, args) in new[]
            {
                (BallPatterns.RingOut, "count=10"), (BallPatterns.RingIn, "count=10"), (BallPatterns.RingAlt, "count=10"),
                (BallPatterns.Random, "count=10"), (BallPatterns.Fan, "count=7"),
            })
            {
                Assert.IsTrue(Size(pattern, args, null) > 4, pattern + " is bigger than 4 uncapped: " + Size(pattern, args, null));
                Assert.IsTrue(Size(pattern, args, 4) <= 4, pattern + " at a live count of 4: " + Size(pattern, args, 4));
                Assert.IsTrue(Size(pattern, args, 4) >= 1, pattern + " still plans a wave");
            }
        }

        [TestMethod]
        public void RowAndCross_CannotShrink_TheirSizeIsTheWallGeometrys()
        {
            // recorded for the event pool: these waves do not depend on count
            foreach (var pattern in new[] { BallPatterns.Row, BallPatterns.Cross })
            {
                var free = Size(pattern, "", null);
                var capped = Size(pattern, "", 4);

                Assert.AreEqual(free, capped, pattern + ": a live count does not change the wall");
                Assert.IsTrue(free > 4, pattern + " at r=60 is " + free + " balls, over a budget of 4");
            }
        }

        [TestMethod]
        public void Rings_NeverShrinkBelowThree_AtAnyBudget()
        {
            foreach (var pattern in new[] { BallPatterns.RingOut, BallPatterns.RingIn, BallPatterns.RingAlt })
                Assert.AreEqual(3, Size(pattern, "", 1), pattern + " at a live count of 1 is still its minimum of 3");
        }

        // ---- the cadence at low counts ----------------------------------------------------------------------------------

        private sealed class W
        {
            public int Size;
        }

        [TestMethod]
        public void AtALiveCountOfFour_NoWaveIsEverBiggerThanFour_NorMoreThanFourInFlight_AndRaisingItTakesEffectAtTheNextDueWave()
        {
            var live = 4;
            var p = P("count=10 waves=8 wave_every=1", BallPatterns.Random);
            var cadence = new BallCadence(p, BallPatterns.Random);
            var rng = new Random(9);
            var sizes = new List<int>();
            var peak = 0;
            var closeAt = new Dictionary<int, double>();

            for (var t = 0.0; t <= 40.0; t += 0.5)
            {
                if (Math.Abs(t - 8.0) < 1e-9)
                    live = 10;

                var now = t;

                cadence.Launch(t,
                    n => new W { Size = BallPatterns.Plan(Arena(), BallPatterns.Random, BallRules.CapToLiveCount(BallRules.ForWave(p, n), live), rng, n).Routes.Count },
                    w => w.Size,
                    () => BallBudget.For(live, p.MaxBalls),
                    (n, w) =>
                    {
                        cadence.Open(n, w.Size);
                        sizes.Add(w.Size);
                        closeAt[n] = now + 2.0;
                        return true;
                    });

                peak = Math.Max(peak, sizes.Count == 0 ? 0 : cadence.BallsInFlight);

                if (t < 8.0)
                    Assert.IsTrue(cadence.BallsInFlight <= 4, "at t=" + t + " in flight " + cadence.BallsInFlight);

                foreach (var n in cadence.OpenWaves.ToList())
                {
                    if (t >= closeAt[n])
                    {
                        var clock = cadence.ClockOf(n);

                        for (var id = 0; id < 20; id++)
                        {
                            clock.OnSpawned(n * 100 + id);
                            clock.OnLaunched(n * 100 + id);
                            clock.OnLegEnd(n * 100 + id);
                        }

                        cadence.Close(n);
                    }
                }
            }

            Assert.IsTrue(sizes.Count >= 4, "waves ran");
            Assert.IsTrue(sizes.Take(4).All(s => s <= 4), "the early waves were planned at 4 or fewer: " + string.Join(",", sizes));
            Assert.IsTrue(sizes.Any(s => s > 4), "after the count rose to 10 a bigger wave was planned: " + string.Join(",", sizes));
            Assert.IsTrue(sizes.Max() <= 10);
        }
    }
}