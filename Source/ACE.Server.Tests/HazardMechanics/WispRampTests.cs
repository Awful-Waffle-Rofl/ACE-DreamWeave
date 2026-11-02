using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using ACE.Server.HazardMechanics;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.HazardMechanics
{
    /// <summary>
    /// The wisps' per-cycle ramp (WispSettings.ForCycle over HazardRamp.At) for move, hold and rest, the hold_floor key
    /// (plan section 7 Q3), and what Validate does with them. Pure: no PropertyManager is touched.
    /// </summary>
    [TestClass]
    public class WispRampTests
    {
        private static HazardRegion Pinnacle() => HazardRegionPresets.Pinnacle(0);

        private static HazardArgs Merged(string args) => HazardArgs.Merge(HazardArgs.Parse(args), HazardArgs.Parse(HazardDefaults.Wisps));

        private static string Validate(string args, string pattern = WispRules.Scatter) => WispRules.Validate(Pinnacle(), Merged(args), pattern);

        [TestMethod]
        public void ForCycle_MoveHoldRestRampLinearly()
        {
            var s = WispSettings.From(Merged("cycles=5 move=3 move_end=1 hold=2 hold_end=1 rest=2 rest_end=0"));

            Assert.AreEqual(3.0, s.ForCycle(1).Move, 1e-9);
            Assert.AreEqual(2.0, s.ForCycle(3).Move, 1e-9);
            Assert.AreEqual(1.0, s.ForCycle(5).Move, 1e-9);

            Assert.AreEqual(2.0, s.ForCycle(1).Hold, 1e-9);
            Assert.AreEqual(1.5, s.ForCycle(3).Hold, 1e-9);
            Assert.AreEqual(1.0, s.ForCycle(5).Hold, 1e-9);

            Assert.AreEqual(2.0, s.ForCycle(1).Rest, 1e-9);
            Assert.AreEqual(1.0, s.ForCycle(3).Rest, 1e-9);
            Assert.AreEqual(0.0, s.ForCycle(5).Rest, 1e-9);

            // an index outside the run clamps to the ends
            Assert.AreEqual(3.0, s.ForCycle(0).Move, 1e-9);
            Assert.AreEqual(1.0, s.ForCycle(99).Move, 1e-9);
        }

        [TestMethod]
        public void ForCycle_WithNoEndKeys_IsTheSettingsThemselves()
        {
            var s = WispSettings.From(Merged("cycles=5"));

            for (var c = 1; c <= 5; c++)
                Assert.AreSame(s, s.ForCycle(c));
        }

        [TestMethod]
        public void ForCycle_OneEndKeyRampsOnlyThatTiming()
        {
            var s = WispSettings.From(Merged("cycles=3 hold=2 hold_end=1"));

            Assert.AreEqual(s.Move, s.ForCycle(3).Move, 1e-9);
            Assert.AreEqual(s.Rest, s.ForCycle(3).Rest, 1e-9);
            Assert.AreEqual(1.0, s.ForCycle(3).Hold, 1e-9);
        }

        [TestMethod]
        public void EndKeys_TakeTheirStartKeysClamp()
        {
            var s = WispSettings.From(HazardArgs.Parse("move_end=999 hold_end=0.001 rest_end=999"));

            Assert.AreEqual(30.0, s.MoveEnd.Value);
            Assert.AreEqual(0.2, s.HoldEnd.Value);
            Assert.AreEqual(60.0, s.RestEnd.Value);
        }

        [TestMethod]
        public void ForCycle_CopiesEveryOtherProperty()
        {
            // every property gets a non-default value, so one ForCycle forgets to copy shows up as a difference
            var rich = WispSettings.From(HazardArgs.Parse(
                "n=5 scale=1.5 move=3 move_end=2 hold=2 hold_end=1.5 burst=2.5 strike_delay=0.7 rest=3 rest_end=2 pillar_radius=2.5 cycles=6 fire=sequence seq_gap=0.5 dir=n sweep_move=2 steps=3 ring_radius=7 cx=4 cy=-9 scatter_cover=0.3 wisp_clear=1.2 wisp_wcid=1006999 script=0x33001029 player_radius=0.6 edge_inset=3 escape_speed=4 hold_floor=0 follow=1 follow_lag=3 mark_dest=1 mark_script=0x33001030 ceiling_beam=1.5 beam_script=0x33001031"));
            var fresh = WispSettings.From(HazardArgs.Empty);
            var ramped = new[] { nameof(WispSettings.Move), nameof(WispSettings.Hold), nameof(WispSettings.Rest) };
            var copy = rich.ForCycle(2);

            Assert.AreNotSame(rich, copy);

            foreach (var prop in typeof(WispSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (ramped.Contains(prop.Name) || prop.Name == nameof(WispSettings.HitWindow))
                    continue; // HitWindow is a constant (+infinity): the same on both, so it cannot show a missed copy

                Assert.AreNotEqual(prop.GetValue(fresh), prop.GetValue(rich), prop.Name + ": the test args must set it to a non-default value");
                Assert.AreEqual(prop.GetValue(rich), prop.GetValue(copy), prop.Name + " was not copied by ForCycle");
            }
        }

        // ---- the script reads the cycle's numbers --------------------------------------------------------------------

        [TestMethod]
        public void Script_BurstTimesUseCycleHold()
        {
            const double dt = 0.1;
            var s = WispSettings.From(Merged("cycles=3 move=3 move_end=1 hold=3 hold_end=1 rest=2 rest_end=0 burst=1"));
            var script = new WispScript(s);
            var glides = new List<(double Now, WispAction Action)>();
            var bursts = new List<(double Now, WispAction Action)>();
            var rests = new List<WispAction>();

            for (var now = dt; now < 200.0 && !script.Ended; now += dt)
            {
                foreach (var a in script.Advance(now, 1))
                {
                    switch (a.Kind)
                    {
                        case WispActionKind.Glide:
                            glides.Add((now, a));
                            script.Glided(now, new[] { 0.0 }, false);
                            break;

                        case WispActionKind.Burst:
                            bursts.Add((now, a));
                            break;

                        case WispActionKind.Rest:
                            rests.Add(a);
                            break;
                    }
                }
            }

            Assert.AreEqual(3, glides.Count);
            Assert.AreEqual(3, bursts.Count);

            for (var i = 0; i < 3; i++)
            {
                var c = s.ForCycle(i + 1);

                Assert.AreEqual(c.Move, glides[i].Action.Seconds, 1e-9, "glide seconds of cycle " + (i + 1));

                var due = glides[i].Now + c.Move + c.Hold;

                Assert.IsTrue(bursts[i].Now >= due - 1e-9 && bursts[i].Now < due + dt + 1e-9, $"cycle {i + 1}: burst at {bursts[i].Now}, due {due}");
            }

            // the rest after cycle k is cycle k's rest: 3 cycles, two rests
            Assert.AreEqual(2, rests.Count);
            Assert.AreEqual(s.ForCycle(1).Rest, rests[0].Seconds, 1e-9);
            Assert.AreEqual(s.ForCycle(2).Rest, rests[1].Seconds, 1e-9);
        }

        [TestMethod]
        public void Director_RandomFireOffsetsSpanTheCyclesHold()
        {
            // fire=random spreads the bursts over 0..hold: with a ramp 3 -> 0.5 the spread must shrink with the cycle
            const double dt = 0.1;
            var (director, port, _) = WispDirectorTests.Setup("cycles=3 move=1 move_end=1 hold=3 hold_end=0.5 burst=0.5 rest=0.5 fire=random n=6 pillar_radius=1", WispRules.Scatter, squad: 6);

            WispDirectorTests.Run(director, port, dt);

            Assert.AreEqual(HazardOutcome.Completed, port.Outcome);
            Assert.AreEqual(3, port.Moves.Count);

            var s = WispDirectorTests.S("cycles=3 move=1 move_end=1 hold=3 hold_end=0.5");

            for (var i = 0; i < 3; i++)
            {
                var c = s.ForCycle(i + 1);
                var from = port.Moves[i].At + c.Move + c.Hold - 1e-9;
                var to = port.Moves[i].At + c.Move + 2.0 * c.Hold + dt + 1e-9;
                var next = i + 1 < 3 ? port.Moves[i + 1].At : double.MaxValue;
                var spawns = port.Spawns.Where(sp => sp.Ok && sp.At >= port.Moves[i].At && sp.At < next).ToList();

                Assert.IsTrue(spawns.Count > 0, "cycle " + (i + 1));
                Assert.IsTrue(spawns.All(sp => sp.At >= from && sp.At <= to), $"cycle {i + 1}: pillars at {string.Join(",", spawns.Select(x => x.At.ToString("0.0")))}, expected within [{from:0.0}, {to:0.0}]");
            }
        }

        // ---- Validate ------------------------------------------------------------------------------------------------

        [TestMethod]
        public void Validate_HoldEndBelowFloor_Refused()
        {
            // floor = 0.5 + (2 + 0.5) / 3 = 1.333 s
            var error = Validate("hold=1.5 hold_end=0.5");

            StringAssert.Contains(error, "hold_end=0.5");
            StringAssert.Contains(error, "too short a telegraph");

            StringAssert.Contains(Validate("hold=1.0 hold_end=2.0"), "hold=1", "a hold under the floor at the START is refused whichever end is lower");
            Assert.IsNull(Validate("hold=1.5 hold_end=1.4"));
            Assert.IsNull(Validate("hold=3 hold_end=1.34"));
        }

        [TestMethod]
        public void Validate_HoldFloorOff_AllowsHalfSecondHold()
        {
            Assert.IsNull(Validate("hold=1.5 hold_end=0.5 hold_floor=0"));
            Assert.IsNull(Validate("hold=0.5 hold_floor=0"));
            Assert.IsNull(Validate("hold=1.5 hold_end=0.5 hold_floor=false"));
        }

        [TestMethod]
        public void Validate_HoldFloorDefault_StillRefuses()
        {
            StringAssert.Contains(Validate("hold=0.5"), "too short a telegraph");
            StringAssert.Contains(Validate("hold=0.5 hold_floor=1"), "too short a telegraph");
            StringAssert.Contains(Validate("hold=1.5 hold_end=0.5 hold_floor=true"), "hold_end");

            // the shipped default string has no ramp and no floor override, so the floor is on
            Assert.IsTrue(WispSettings.From(HazardArgs.Parse(HazardDefaults.Wisps)).HoldFloor);
        }

        [TestMethod]
        public void Validate_FollowLagBelowFloor_Refused()
        {
            // floor 1.333 s: a follower that close would be on a player before they could react
            var error = Validate("follow=1 follow_lag=1.0");

            StringAssert.Contains(error, "follow_lag=1");
            StringAssert.Contains(error, "too short");

            Assert.IsNull(Validate("follow=1 follow_lag=1.4"));
            Assert.IsNull(Validate("follow=1"), "the default lag of 2 s is over the floor");
            Assert.IsNull(Validate("follow=0 follow_lag=1.0"), "no follower, no check");
        }

        [TestMethod]
        public void Validate_FollowLagBelowFloor_AllowedWithHoldFloorOff()
            => Assert.IsNull(Validate("follow=1 follow_lag=0.5 hold_floor=0"));

        [TestMethod]
        public void Validate_RefusesATypoInAFlag_AndANonNumericRampKey()
        {
            StringAssert.Contains(Validate("hold_floor=maybe"), "hold_floor");
            StringAssert.Contains(Validate("follow=sure"), "follow");

            foreach (var key in new[] { "move_end", "hold_end", "rest_end", "follow_lag" })
                StringAssert.Contains(Validate(key + "=fast"), key);
        }

        [TestMethod]
        public void Validate_TheRetiredFollowEveryKey_IsIgnored()
        {
            // follow_every was retired 2026-10-19 (the follower bursts on the crew's beat). A shard row still carrying it
            // must not refuse the run, whatever its value.
            Assert.IsNull(Validate("follow=1 follow_every=3"));
            Assert.IsNull(Validate("follow=1 follow_every=fast"));
        }

        [TestMethod]
        public void Validate_RunCap_UsesStartTimings()
        {
            // 100 cycles x (2.5 + 1.5 + 1.5 + 1) = 650 s at the START timings: over the 600 s cap, whatever the ends are
            StringAssert.Contains(Validate("cycles=100 move_end=0.5 hold_end=1.4 rest_end=0"), "would last");

            // 50 cycles x 6.5 s = 325 s at the start timings: accepted, even though the (slow) end timings would not be
            Assert.IsNull(Validate("cycles=50 move_end=30 hold_end=60 rest_end=60"));
        }
    }
}
