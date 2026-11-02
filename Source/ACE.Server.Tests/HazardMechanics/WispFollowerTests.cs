using System;
using System.Linq;
using System.Numerics;

using ACE.Server.HazardMechanics;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using FakeHandle = ACE.Server.Tests.HazardMechanics.WispDirectorTests.FakeHandle;

namespace ACE.Server.Tests.HazardMechanics
{
    /// <summary>
    /// The follower wisp: WispTrail and WispFollowRules (pure), and the director driving one through the recording fake
    /// of the world. Pure: no PropertyManager is touched.
    /// </summary>
    [TestClass]
    public class WispFollowerTests
    {
        private const double Lag = 2.0;
        private const double Step = WispFollowRules.LegStep;

        /// <summary>A target that runs along +x at <paramref name="speed"/> m/s until <paramref name="stopAt"/> and then stands.</summary>
        private static Vector2 Target(double t, double speed, double stopAt) => new Vector2((float)(speed * Math.Min(t, stopAt)), 0f);

        /// <summary>Records the target every 0.1 s up to <paramref name="until"/> and returns the trail.</summary>
        private static WispTrail Record(double speed, double stopAt, double until)
        {
            var trail = new WispTrail();

            for (var t = 0.0; t <= until + 1e-9; t += 0.1)
                trail.Record(t, Target(t, speed, stopAt));

            return trail;
        }

        // ---- WispTrail -----------------------------------------------------------------------------------------------

        [TestMethod]
        public void Trail_InterpolatesRecordedPath()
        {
            var trail = new WispTrail();
            trail.Record(0.0, new Vector2(0f, 0f));
            trail.Record(2.0, new Vector2(10f, 0f));
            trail.Record(3.0, new Vector2(10f, 4f));

            Assert.AreEqual(new Vector2(5f, 0f), trail.At(1.0).Value);
            Assert.AreEqual(new Vector2(2.5f, 0f), trail.At(0.5).Value);
            Assert.AreEqual(new Vector2(10f, 2f), trail.At(2.5).Value);
            Assert.AreEqual(new Vector2(10f, 4f), trail.At(3.0).Value);
            Assert.AreEqual(new Vector2(10f, 4f), trail.At(99.0).Value, "after the last sample the target is where it last was");
        }

        [TestMethod]
        public void Trail_BeforeFirstSample_IsFirst()
        {
            var trail = new WispTrail();

            Assert.IsNull(trail.At(0.0), "an empty trail knows nothing");

            trail.Record(5.0, new Vector2(3f, 4f));
            trail.Record(6.0, new Vector2(7f, 4f));

            Assert.AreEqual(new Vector2(3f, 4f), trail.At(0.0).Value);
            Assert.AreEqual(new Vector2(3f, 4f), trail.At(5.0).Value);
        }

        [TestMethod]
        public void Trail_DropsSamplesOlderThanTheWindow_ButKeepsTheAnchor()
        {
            var trail = new WispTrail(retainSeconds: 3.0);

            for (var t = 0; t <= 20; t++)
                trail.Record(t, new Vector2(t * 2f, 0f));

            Assert.IsTrue(trail.Count <= 5, "kept " + trail.Count + " samples of 21");
            Assert.AreEqual(new Vector2(34f, 0f), trail.At(17.0).Value, "the edge of the window is still exact");
            Assert.AreEqual(new Vector2(35f, 0f), trail.At(17.5).Value, "and interpolated between kept samples");
            Assert.AreEqual(new Vector2(34f, 0f), trail.At(0.0).Value, "older than everything kept: the oldest kept sample");
        }

        [TestMethod]
        public void Trail_Clear_ForgetsEverything()
        {
            var trail = new WispTrail();
            trail.Record(1.0, new Vector2(1f, 1f));
            trail.Clear();

            Assert.AreEqual(0, trail.Count);
            Assert.IsNull(trail.At(1.0));

            trail.Record(50.0, new Vector2(9f, 9f));
            Assert.AreEqual(new Vector2(9f, 9f), trail.At(0.0).Value, "the next sighting starts a fresh trail");
        }

        [TestMethod]
        public void Trail_IgnoresAnOlderSample_AndReplacesASameTimeOne()
        {
            var trail = new WispTrail();
            trail.Record(5.0, new Vector2(1f, 1f));
            trail.Record(4.0, new Vector2(9f, 9f));
            trail.Record(5.0, new Vector2(2f, 2f));

            Assert.AreEqual(1, trail.Count);
            Assert.AreEqual(new Vector2(2f, 2f), trail.At(5.0).Value);
        }

        // ---- WispFollowRules -----------------------------------------------------------------------------------------

        [TestMethod]
        public void NextLeg_TargetsLaggedPoint()
        {
            var trail = Record(speed: 2.0, stopAt: 99.0, until: 5.0);

            var leg = WispFollowRules.NextLeg(trail, 5.0, Lag, Step, double.NegativeInfinity);

            Assert.IsTrue(leg.HasValue);
            Assert.AreEqual(Step, leg.Value.Seconds);
            Assert.AreEqual(2.0 * (5.0 + Step - Lag), leg.Value.End.X, 1e-3, "the target's position at now + step - lag");
            Assert.AreEqual(0f, leg.Value.End.Y);

            Assert.IsNull(WispFollowRules.NextLeg(new WispTrail(), 5.0, Lag, Step, double.NegativeInfinity), "no samples, no leg");
            Assert.IsNull(WispFollowRules.NextLeg(null, 5.0, Lag, Step, double.NegativeInfinity), "no trail, no leg");
        }

        [TestMethod]
        public void NextLeg_IsDueOneTickBeforeTheCurrentLegEnds()
        {
            var trail = Record(2.0, 99.0, 5.0);

            Assert.IsNull(WispFollowRules.NextLeg(trail, 5.0, Lag, Step, legEndsAt: 5.3), "the leg in flight has 0.3 s to go");
            Assert.IsNotNull(WispFollowRules.NextLeg(trail, 5.0, Lag, Step, legEndsAt: 5.1), "one tick left: chain the next leg now");
            Assert.IsNotNull(WispFollowRules.NextLeg(trail, 5.0, Lag, Step, legEndsAt: 4.0), "the last leg is over");
            Assert.IsNotNull(WispFollowRules.NextLeg(trail, 5.0, Lag, Step, legEndsAt: 5.3, tick: 0.5), "a slower tick chains earlier");
        }

        [TestMethod]
        public void BeatPoint_IsWhereTheTargetStoodLagBeforeTheBurst()
        {
            var trail = Record(speed: 2.0, stopAt: 99.0, until: 10.0);

            var point = WispFollowRules.BeatPoint(trail, followStartedAt: 0.0, burstAt: 9.0, lag: Lag, now: 7.5);

            Assert.IsTrue(point.HasValue);
            Assert.AreEqual(2.0 * (9.0 - Lag), point.Value.X, 1e-3, "the target's position lag seconds before the burst");
            Assert.AreEqual(0f, point.Value.Y);
        }

        [TestMethod]
        public void BeatPoint_AFreshSightingIsWarnedFirst()
        {
            var trail = Record(2.0, 99.0, 10.0);

            Assert.IsNull(WispFollowRules.BeatPoint(trail, followStartedAt: 8.0, burstAt: 9.0, lag: Lag, now: 7.5), "sighted 1 s before the burst: under one lag of warning, no burst");
            Assert.IsNotNull(WispFollowRules.BeatPoint(trail, followStartedAt: 7.0, burstAt: 9.0, lag: Lag, now: 7.5), "sighted exactly one lag before: it bursts");
            Assert.IsNull(WispFollowRules.BeatPoint(trail, followStartedAt: double.NaN, burstAt: 9.0, lag: Lag, now: 7.5), "nobody followed");
            Assert.IsNull(WispFollowRules.BeatPoint(new WispTrail(), 0.0, 9.0, Lag, 7.5), "no samples");
            Assert.IsNull(WispFollowRules.BeatPoint(null, 0.0, 9.0, Lag, 7.5), "no trail");
        }

        [TestMethod]
        public void BeatPoint_LagShorterThanTheHold_IsTheNewestKnownPosition_NotAFutureOne()
        {
            // the hold starts at 10 (now) and the burst comes at 11.5; with lag 1, burstAt - lag = 10.5 is still in the
            // future, so the point is clamped to now: where the target is at the hold start
            var trail = Record(speed: 2.0, stopAt: 99.0, until: 10.0);

            var point = WispFollowRules.BeatPoint(trail, followStartedAt: 0.0, burstAt: 11.5, lag: 1.0, now: 10.0);

            Assert.IsTrue(point.HasValue);
            Assert.AreEqual(2.0 * 10.0, point.Value.X, 1e-3, "the newest sample, never a point past now");

            // a later sample (a caller that recorded past now) must not move it: the query time is min(burstAt - lag, now)
            trail.Record(10.4, new Vector2(99f, 0f));
            Assert.AreEqual(2.0 * 10.0, WispFollowRules.BeatPoint(trail, 0.0, 11.5, 1.0, now: 10.0).Value.X, 1e-3);

            // with lag longer than the time left, the lagged point is in the past and used as is
            Assert.AreEqual(2.0 * 9.5, WispFollowRules.BeatPoint(trail, 0.0, 11.5, 2.0, now: 10.0).Value.X, 1e-3);
        }
        /// <summary>The distance between the follower (at the end of the leg issued at <paramref name="now"/>, on arrival) and the target when it arrives.</summary>
        private static double GapOnArrival(double speed, double stopAt, double now)
        {
            var trail = Record(speed, stopAt, now);
            var leg = WispFollowRules.NextLeg(trail, now, Lag, Step, double.NegativeInfinity).Value;
            var arrival = now + leg.Seconds;

            return Vector2.Distance(leg.End, Target(arrival, speed, stopAt));
        }

        [TestMethod]
        public void StandingStillTwoSeconds_FollowerWithinReach()
        {
            // pillar_radius 2 + player_radius 0.5; the target runs at 5 m/s, then stands from t = 4
            var reach = 2.0 + 0.5;

            // a leg issued at 5.5 ends at 6.0 = standing + lag, at the target's resting place
            Assert.IsTrue(GapOnArrival(5.0, 4.0, 5.5) <= reach, "gap " + GapOnArrival(5.0, 4.0, 5.5));
            Assert.IsTrue(GapOnArrival(5.0, 4.0, 7.0) <= reach);
            Assert.AreEqual(0.0, GapOnArrival(5.0, 4.0, 7.0), 1e-3, "long after it stopped the follower is exactly under the target");
        }

        [TestMethod]
        public void RunningAway_FollowerOutsideReach()
        {
            var reach = 2.0 + 0.5;

            // running at 5 m/s the follower always trails by 5 x lag = 10 m
            Assert.AreEqual(5.0 * Lag, GapOnArrival(5.0, 99.0, 10.0), 1e-2);
            Assert.IsTrue(GapOnArrival(5.0, 99.0, 10.0) > reach);

            // a slow walker (under reach / lag = 1.25 m/s) is inside it
            Assert.IsTrue(GapOnArrival(1.0, 99.0, 10.0) < reach);
        }

        // ---- the director --------------------------------------------------------------------------------------------

        private const string Args = "follow=1 cycles=2 move=1 hold=1.5 burst=1 rest=0.5 n=3 follow_lag=2";

        /// <summary>A longer run, so the leave/return and target-switch tests have beats on both sides of the change.</summary>
        private const string LongArgs = "follow=1 cycles=10 move=1 hold=1.5 burst=1 rest=0.5 n=3 follow_lag=2";

        /// <summary>The Grael P2 shape (ramping move, hold and rest) with a small crew.</summary>
        private const string RampArgs = "follow=1 cycles=6 move=2.5 move_end=1.5 hold=1.5 hold_end=0.5 hold_floor=0 rest=1.0 rest_end=0.5 burst=1 n=3 follow_lag=2 mark_dest=1";

        private static System.Collections.Generic.List<(double At, Vector3 Point)> FollowerPillars(WispDirectorTests.FakePort port, float x, float y)
            => port.Spawns.Where(sp => sp.Ok && IsAt(sp.Point, x, y)).Select(sp => (sp.At, sp.Point)).ToList();

        private static System.Collections.Generic.List<double> CrewPillarTimes(WispDirectorTests.FakePort port, float x, float y)
            => port.Spawns.Where(sp => sp.Ok && !IsAt(sp.Point, x, y)).Select(sp => sp.At).ToList();

        private static bool IsAt(Vector3 p, float x, float y) => Math.Abs(p.X - x) < 1e-3 && Math.Abs(p.Y - y) < 1e-3;

        /// <summary>The first crew burst of each step: the earliest crew pillar after each glide starts and before the next one.</summary>
        private static System.Collections.Generic.List<double> FirstCrewBurstPerStep(WispDirectorTests.FakePort port, float x, float y)
        {
            var glides = port.Moves.Select(m => m.At).OrderBy(a => a).ToList();
            var crew = CrewPillarTimes(port, x, y);
            var firsts = new System.Collections.Generic.List<double>();

            for (var i = 0; i < glides.Count; i++)
            {
                var until = i + 1 < glides.Count ? glides[i + 1] : double.PositiveInfinity;
                var inStep = crew.Where(c => c >= glides[i] - 1e-9 && c < until - 1e-9).ToList();

                if (inStep.Count > 0)
                    firsts.Add(inStep.Min());
            }

            return firsts;
        }

        [TestMethod]
        public void NoTarget_FollowerIdles()
        {
            var follower = new FakeHandle { Id = 99, Pos = new Vector3(0f, -30f, -51f) };
            var (director, port, _) = WispDirectorTests.Setup(Args, WispRules.Scatter, squad: 3, follower: follower);

            // port.Target is null: nobody is in the region
            WispDirectorTests.Run(director, port, 0.1);

            Assert.AreEqual(HazardOutcome.Completed, port.Outcome);
            Assert.AreEqual(0, port.Legs.Count, "an idle follower is never moved");
            Assert.AreEqual(new Vector3(0f, -30f, -51f), follower.Pos);
            Assert.AreEqual(2 * 3, port.Hosts.Count, "only the crew's pillars: 2 cycles x 3 wisps");
        }

        [TestMethod]
        public void Director_FollowerBurstsOnTheCrewsBeat_AcrossTheRamp()
        {
            // 2026-10-19 playtest: the follower read as a different tempo. Its bursts must be the crew's bursts.
            var follower = new FakeHandle { Id = 99, Pos = new Vector3(0f, -30f, -51f) };
            var (director, port, _) = WispDirectorTests.Setup(RampArgs, WispRules.Scatter, squad: 3, follower: follower);

            port.Target = _ => new Vector3(20f, -30f, -51f);

            WispDirectorTests.Run(director, port, 0.1);

            Assert.AreEqual(HazardOutcome.Completed, port.Outcome);

            var followerTimes = FollowerPillars(port, 20f, -30f).Select(p => p.At).ToList();
            var crewFirsts = FirstCrewBurstPerStep(port, 20f, -30f);

            Assert.IsTrue(crewFirsts.Count >= 6, "one crew beat per step over 6 cycles: " + crewFirsts.Count);
            Assert.AreEqual(crewFirsts.Count, followerTimes.Count,
                $"one follower burst per crew beat (sighted at 0.1 s, the first beat is more than follow_lag later). follower [{string.Join(", ", followerTimes)}] crew [{string.Join(", ", crewFirsts)}]");

            for (var i = 0; i < crewFirsts.Count; i++)
                Assert.AreEqual(crewFirsts[i], followerTimes[i], 1e-9, $"beat {i}: the follower bursts in the same tick as the step's first crew burst");

            // and the beat really ramps: the gaps between beats shrink as move, hold and rest ramp down
            var gaps = followerTimes.Zip(followerTimes.Skip(1), (a, b) => b - a).ToList();

            Assert.IsTrue(gaps.First() - gaps.Last() > 1.0, $"the beat tightens with the ramp: first gap {gaps.First():0.##} s, last {gaps.Last():0.##} s");

            // the warning window: the follower's mark goes down at the crew's hold start and stands until its burst
            var marks = port.DestMarks.Where(d => IsAt(d.Point, 20f, -30f)).ToList();

            Assert.AreEqual(followerTimes.Count, marks.Count, "one follower mark per follower burst");

            var holdStarts = port.Moves.Select(m => m.At + m.Seconds).ToList();

            for (var i = 0; i < marks.Count; i++)
            {
                Assert.IsTrue(holdStarts.Any(h => marks[i].At >= h - 1e-9 && marks[i].At < h + 0.1 + 1e-9), $"mark {i} at {marks[i].At} goes down at a crew hold start (within one tick)");
                // within one tick, as the crew's own marks are (WispDirectorTests: the burst fires in the tick at or after its time)
                Assert.AreEqual(followerTimes[i], marks[i].At + marks[i].Seconds, 0.1 + 1e-9, $"mark {i} stands until its burst");
            }
        }

        [TestMethod]
        public void Director_ATargetLostAfterTheHoldStart_LeavesNoMarkStanding_AndNoBurst()
        {
            // review R2: the beat locks (and marks) at the hold start; if the target then leaves before the burst, the
            // beat is abandoned, so its mark must go too, or it is a telegraph with no burst under it
            var follower = new FakeHandle { Id = 99, Pos = new Vector3(0f, -30f, -51f) };
            var (director, port, _) = WispDirectorTests.Setup("follow=1 cycles=1 move=1 hold=2 burst=1 rest=0.5 n=3 follow_lag=2 mark_dest=1", WispRules.Scatter, squad: 3, follower: follower);

            // present until the middle of the hold (glide 0.1..1.1, hold 1.1..3.1), gone before the burst
            var leaveAt = 2.0;
            var burstAt = double.NaN;

            port.Target = t => t < leaveAt ? new Vector3(20f, -30f, -51f) : (Vector3?)null;

            WispDirectorTests.Run(director, port, 0.1, t =>
            {
                // the first crew pillar of the step is the burst the follower would have fired with
                if (double.IsNaN(burstAt) && port.Spawns.Count > 0)
                    burstAt = port.Spawns[0].At;
            });

            var marks = port.MarkHandles.Where(m => IsAt(m.Pos, 20f, -30f)).ToList();

            Assert.AreEqual(1, marks.Count, "control: the beat really locked and marked at the hold start, while the target was present");
            Assert.IsTrue(port.DestMarks.Single(d => IsAt(d.Point, 20f, -30f)).At < leaveAt, "the mark went down before the target left");
            Assert.AreEqual(0, FollowerPillars(port, 20f, -30f).Count, "no follower burst once the target is gone");
            Assert.IsFalse(double.IsNaN(burstAt), "control: the crew did burst");

            port.Now = burstAt;
            Assert.IsFalse(port.Alive(marks[0]), "no follower mark is still standing at the burst");
            Assert.IsTrue(port.Destroyed.Any(d => ReferenceEquals(d.Handle, marks[0]) && d.At < burstAt), "it was removed when the beat was abandoned, before the burst");
        }

        [TestMethod]
        public void Director_ABurstThatFires_LeavesItsMarkToBurnOut()
        {
            var follower = new FakeHandle { Id = 99, Pos = new Vector3(0f, -30f, -51f) };
            var (director, port, _) = WispDirectorTests.Setup(RampArgs, WispRules.Scatter, squad: 3, follower: follower);

            port.Target = _ => new Vector3(20f, -30f, -51f);

            WispDirectorTests.Run(director, port, 0.1);

            Assert.IsTrue(port.MarkHandles.Any(m => IsAt(m.Pos, 20f, -30f)), "control: follower marks were placed");
            Assert.AreEqual(0, port.Destroyed.Count, "a mark whose burst fires is never destroyed early");
        }
        [TestMethod]
        public void Director_FollowerSitsStillThroughTheHold()
        {
            var follower = new FakeHandle { Id = 99, Pos = new Vector3(0f, -30f, -51f) };
            var (director, port, _) = WispDirectorTests.Setup(RampArgs, WispRules.Scatter, squad: 3, follower: follower);

            // the target keeps walking, so a trailing follower would keep issuing legs
            port.Target = t => new Vector3(10f + (float)(0.5 * t), -30f, -51f);

            WispDirectorTests.Run(director, port, 0.1);

            var bursts = port.Spawns.Where(sp => sp.Ok).Select(sp => sp.At).ToList();

            foreach (var move in port.Moves)
            {
                var holdAt = move.At + move.Seconds;
                var burstAt = bursts.Where(b => b >= holdAt - 1e-9).DefaultIfEmpty(double.NaN).Min();

                if (double.IsNaN(burstAt))
                    continue;

                // one settle leg at the hold start, then nothing until the burst
                var during = port.Legs.Where(l => l.At > holdAt + 0.1 + 1e-9 && l.At < burstAt - 1e-9).ToList();

                Assert.AreEqual(0, during.Count, $"no trailing leg during the hold {holdAt:0.##}..{burstAt:0.##}: legs at {string.Join(", ", during.Select(l => l.At))}");
            }
        }

        [TestMethod]
        public void Director_FollowerBurstUsesStrikeHelper_AndPlaceWisp()
        {
            // the target stands inside the Pinnacle's west block (x 5..15, y -25..-15), where a wisp at hazard height would be in a solid
            var follower = new FakeHandle { Id = 99, Pos = new Vector3(0f, -20f, -51f) };
            var (director, port, _) = WispDirectorTests.Setup(LongArgs, WispRules.Scatter, squad: 3, follower: follower);
            var s = WispDirectorTests.S(LongArgs);

            port.Target = _ => new Vector3(10f, -20f, -51f);

            WispDirectorTests.Run(director, port, 0.1);

            Assert.AreEqual(HazardOutcome.Completed, port.Outcome);
            Assert.IsTrue(port.Legs.Count >= 4, "the follower flies leg after leg: " + port.Legs.Count);

            // PlaceWisp: the follower stands short of the solid (x <= 5, backed off), never inside it
            foreach (var leg in port.Legs)
            {
                Assert.AreSame(follower, leg.Handle);
                Assert.IsTrue(leg.Seconds >= WispFollowRules.LegStep - 1e-9, "a leg is never shorter than one step");
                Assert.IsTrue(WispDirectorTests.BlockHasCell(leg.End), $"the follower was sent INTO a solid at {leg.End}");
                Assert.IsTrue(leg.End.X < 5f, "stands short of the block: " + leg.End.X);
                Assert.AreEqual(-51f, leg.End.Z, 1e-4, "at the hazard height, never the raised pillar point");
            }

            // the pillar fires at the TRUE target, raised onto the solid's top (z -48), on crew beats
            var pillars = port.Spawns.Where(sp => sp.Ok && IsAt(sp.Point, 10f, -20f)).ToList();
            var crewFirsts = FirstCrewBurstPerStep(port, 10f, -20f);

            Assert.IsTrue(pillars.Count >= 2, "a burst on each crew beat: " + pillars.Count);
            Assert.IsTrue(pillars.All(p => Math.Abs(p.Point.Z + 48f) < 1e-4), "raised onto the top of the block");
            Assert.IsTrue(pillars.All(p => crewFirsts.Any(c => Math.Abs(c - p.At) < 1e-9)), "every follower burst is a crew beat");

            // and each one is struck through the one strike helper with the shared radius and window, from its own host
            foreach (var p in pillars)
            {
                var host = port.Hosts.Single(h => Math.Abs(h.Pos.X - p.Point.X) < 1e-3 && Math.Abs(h.Pos.Y - p.Point.Y) < 1e-3 && Math.Abs(h.Until - (p.At + s.Burst)) < 1e-6);
                var strikes = port.Strikes.Where(st => ReferenceEquals(st.Source, host)).ToList();

                Assert.IsTrue(strikes.Count > 0, "the follower's pillar damages");
                Assert.IsTrue(strikes.All(st => st.Radius == s.HitRadius && double.IsPositiveInfinity(st.Window)));
            }
        }

        [TestMethod]
        public void Director_TargetLeavesAndReturns_IsAFreshSighting_WithAFreshTelegraph()
        {
            var follower = new FakeHandle { Id = 99, Pos = new Vector3(0f, -30f, -51f) };
            var (director, port, _) = WispDirectorTests.Setup(LongArgs, WispRules.Scatter, squad: 3, follower: follower);

            // present 0-10 s, gone 10-20 s, back from 20 s, standing at (20, -30) all the while
            port.Target = t => t >= 10.0 && t < 20.0 ? (Vector3?)null : new Vector3(20f, -30f, -51f);

            WispDirectorTests.Run(director, port, 0.1);

            var pillars = FollowerPillars(port, 20f, -30f);

            Assert.IsTrue(pillars.Any(p => p.At < 10.0), "bursts before the target left");
            Assert.IsFalse(pillars.Any(p => p.At > 10.05 && p.At < 20.0 + Lag - 0.15), "no burst while it was gone, and none within one lag of its return");

            var after = pillars.Where(p => p.At >= 20.0).ToList();

            Assert.IsTrue(after.Count > 0, "and bursts resume");
            Assert.IsTrue(after[0].At >= 20.0 + Lag - 0.15, "the first burst after the return is at least follow_lag after it: " + after[0].At);
        }

        [TestMethod]
        public void Director_FirstBurstIsAtLeastLagAfterTheSighting_AndNoLegExceedsTheSpeedCap()
        {
            var follower = new FakeHandle { Id = 99, Pos = new Vector3(0f, -30f, -51f) };
            var (director, port, _) = WispDirectorTests.Setup("follow=1 cycles=4 move=0.5 hold=0.5 burst=1 rest=0.5 n=3 follow_lag=2", WispRules.Scatter, squad: 3, follower: follower);

            port.Target = _ => new Vector3(30f, -30f, -51f); // 30 m from the follower: the first leg would otherwise be 60 m/s

            var from = follower.Pos;
            WispDirectorTests.Run(director, port, 0.1);

            Assert.IsTrue(port.Legs.Count > 3);

            foreach (var leg in port.Legs)
            {
                var speed = Vector3.Distance(from, leg.End) / leg.Seconds;

                Assert.IsTrue(speed <= WispFollowRules.FollowMaxSpeed + 1e-3, $"leg at t={leg.At} flew {speed:0.0} m/s");
                from = leg.End;
            }

            Assert.IsTrue(port.Legs[0].Seconds >= 30.0 / WispFollowRules.FollowMaxSpeed - 1e-6, "the long first leg takes longer than a step");

            var pillars = FollowerPillars(port, 30f, -30f);
            var crewFirsts = FirstCrewBurstPerStep(port, 30f, -30f);

            Assert.IsTrue(pillars.Count > 0);
            Assert.IsTrue(pillars[0].At >= 0.1 + Lag - 0.05, "first burst " + pillars[0].At + ", sighting at 0.1, lag 2");
            Assert.IsTrue(crewFirsts[0] < 0.1 + Lag, "control: the crew's first beat (" + crewFirsts[0] + ") came inside the lag, so a beat really was skipped");
            Assert.AreEqual(crewFirsts.First(c => c >= 0.1 + Lag - 1e-9), pillars[0].At, 1e-9, "and the first burst is the first crew beat after the lag");
        }

        [TestMethod]
        public void Director_ChainsLegsByThePortsTick()
        {
            int Legs(double tick)
            {
                var follower = new FakeHandle { Id = 99, Pos = new Vector3(20f, -30f, -51f) };
                var (director, port, _) = WispDirectorTests.Setup(Args, WispRules.Scatter, squad: 3, follower: follower);

                port.TickSeconds = tick;
                port.Target = _ => new Vector3(20f, -30f, -51f);
                WispDirectorTests.Run(director, port, 0.1);

                return port.Legs.Count;
            }

            // a slow tick must chain earlier (every tick), a fast one only in the last 0.1 s of each 0.5 s leg
            Assert.IsTrue(Legs(0.5) > Legs(0.1), "the director must pass the port tick to NextLeg");
        }

        [TestMethod]
        public void Director_ANewFirstPlayer_RestartsTheSession()
        {
            var follower = new FakeHandle { Id = 99, Pos = new Vector3(0f, -30f, -51f) };
            var (director, port, _) = WispDirectorTests.Setup(LongArgs, WispRules.Scatter, squad: 3, follower: follower);

            port.Target = _ => new Vector3(20f, -30f, -51f);
            port.TargetId = t => t < 6.0 ? 1u : 2u;

            WispDirectorTests.Run(director, port, 0.1);

            var pillars = FollowerPillars(port, 20f, -30f);

            Assert.IsTrue(pillars.Any(p => p.At < 6.0), "player 1 was hit-telegraphed before the switch");
            Assert.IsFalse(pillars.Any(p => p.At > 6.05 && p.At < 6.0 + Lag - 0.15), "player 2 gets a fresh telegraph, not player 1's schedule");
            Assert.IsTrue(pillars.Any(p => p.At >= 6.0 + Lag - 0.15), "and bursts resume for player 2");
        }

        [TestMethod]
        public void Director_TheFollowerIsNotInTheCrew_AndDoesNotCountTowardCycles()
        {
            var follower = new FakeHandle { Id = 99, Pos = new Vector3(0f, -30f, -51f) };
            var (director, port, _) = WispDirectorTests.Setup(Args, WispRules.Scatter, squad: 3, follower: follower);

            port.Target = _ => new Vector3(20f, -30f, -51f);

            WispDirectorTests.Run(director, port, 0.1);

            Assert.AreEqual(2, port.Moves.Count, "one MoveLegs per cycle, as without a follower");
            Assert.IsTrue(port.Moves.All(m => m.Group.All(g => !ReferenceEquals(g.Handle, follower))), "the follower is never in the crew's glide");
            Assert.AreEqual(2, port.Logs.Count(l => l.StartsWith("cycle ") && l.Contains("pattern=")));
        }

        [TestMethod]
        public void Director_ADeadFollowerIsDropped_AndTheRunCarriesOn()
        {
            var follower = new FakeHandle { Id = 99, Pos = new Vector3(0f, -30f, -51f) };
            var (director, port, _) = WispDirectorTests.Setup(Args, WispRules.Scatter, squad: 3, follower: follower);

            port.Target = _ => new Vector3(20f, -30f, -51f);

            WispDirectorTests.Run(director, port, 0.1, t => { if (t >= 2.0) follower.Dead = true; });

            Assert.AreEqual(HazardOutcome.Completed, port.Outcome);
            Assert.AreEqual(1, port.Logs.Count(l => l.Contains("follower wisp is gone")));
            Assert.IsTrue(port.Legs.All(l => l.At < 2.0 + 1e-9), "no leg after it died");
        }

        [TestMethod]
        public void Director_FollowOff_IgnoresAFollowerHandle()
        {
            var follower = new FakeHandle { Id = 99, Pos = new Vector3(0f, -30f, -51f) };
            var (director, port, _) = WispDirectorTests.Setup("cycles=2 move=1 hold=1.5 burst=1 rest=0.5 n=3", WispRules.Scatter, squad: 3, follower: follower);

            port.Target = _ => new Vector3(20f, -30f, -51f);

            WispDirectorTests.Run(director, port, 0.1);

            Assert.AreEqual(0, port.Legs.Count);
        }
    }
}
