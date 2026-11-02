using System;
using System.Numerics;

using ACE.Server.Pinnacles;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.Pinnacles
{
    /// <summary>
    /// PinnacleFacing (section 7 Q4). ACE convention: identity faces +Y (north); heading = atan2(-dx, dy) about +Z.
    /// </summary>
    [TestClass]
    public class PinnacleFacingTests
    {
        private const float Eps = 1e-4f;

        /// <summary>The Grael head's spawn rotation, W,X,Y,Z = (0.704729, 0.057939, 0.057939, 0.704729).</summary>
        private static readonly Quaternion HeadTilt = Quaternion.Normalize(new Quaternion(0.057939f, 0.057939f, 0.704729f, 0.704729f));

        private static void AssertVec(Vector3 expected, Vector3 actual, string what)
        {
            Assert.AreEqual(expected.X, actual.X, Eps, what + " x");
            Assert.AreEqual(expected.Y, actual.Y, Eps, what + " y");
            Assert.AreEqual(expected.Z, actual.Z, Eps, what + " z");
        }

        [TestMethod]
        public void YawToward_North_IsZero()
        {
            Assert.AreEqual(0f, PinnacleFacing.YawToward(new Vector2(5, 5), new Vector2(5, 9)), Eps);
        }

        [TestMethod]
        public void YawToward_East_IsMinusHalfPi_WestPlus()
        {
            Assert.AreEqual(-MathF.PI / 2, PinnacleFacing.YawToward(Vector2.Zero, new Vector2(3, 0)), Eps);
            Assert.AreEqual(MathF.PI / 2, PinnacleFacing.YawToward(Vector2.Zero, new Vector2(-3, 0)), Eps);
            Assert.AreEqual(MathF.PI, Math.Abs(PinnacleFacing.YawToward(Vector2.Zero, new Vector2(0, -3))), Eps);
            Assert.AreEqual(0f, PinnacleFacing.YawToward(new Vector2(1, 1), new Vector2(1, 1)), "coincident points");
        }

        [TestMethod]
        public void ComposeFacing_IdentityBase_ForwardPointsAtTarget()
        {
            var east = PinnacleFacing.ComposeFacing(PinnacleFacing.YawToward(Vector2.Zero, new Vector2(10, 0)), Quaternion.Identity);
            AssertVec(Vector3.UnitX, Vector3.Transform(Vector3.UnitY, east), "east");

            var north = PinnacleFacing.ComposeFacing(PinnacleFacing.YawToward(Vector2.Zero, new Vector2(0, 10)), Quaternion.Identity);
            AssertVec(Vector3.UnitY, Vector3.Transform(Vector3.UnitY, north), "north");
        }

        [TestMethod]
        [DataRow(0f)]
        [DataRow(-1.5707964f)]
        [DataRow(1.0f)]
        [DataRow(3.0f)]
        public void ComposeFacing_PreservesTilt(float yaw)
        {
            var composed = PinnacleFacing.ComposeFacing(yaw, HeadTilt);
            var yawOnly = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, yaw);

            // the composed rotation applied to the base up axis == the tilted up axis rotated by the yaw
            var expectedUp = Vector3.Transform(Vector3.Transform(Vector3.UnitZ, HeadTilt), yawOnly);
            AssertVec(expectedUp, Vector3.Transform(Vector3.UnitZ, composed), "up");

            // and so the tilt from vertical is unchanged
            var tiltBefore = Vector3.Transform(Vector3.UnitZ, HeadTilt).Z;
            Assert.AreEqual(tiltBefore, Vector3.Transform(Vector3.UnitZ, composed).Z, Eps);
            Assert.IsTrue(tiltBefore < 0.999f, "the fixture really is tilted");

            Assert.AreEqual(1f, composed.Length(), Eps, "normalized");
        }

        [TestMethod]
        public void FaceToward_TiltedHead_FacesEastAndNorth()
        {
            var head = new Vector2(10.6544f, -20.161f);

            foreach (var target in new[] { new Vector2(32.5f, -20f), new Vector2(10.6544f, 0f), new Vector2(0f, -30f) })
            {
                var r = PinnacleFacing.FaceToward(HeadTilt, head, target);

                Assert.AreEqual(PinnacleFacing.YawToward(head, target), PinnacleFacing.YawOf(r), Eps, $"faces {target}");
                Assert.AreEqual(Vector3.Transform(Vector3.UnitZ, HeadTilt).Z, Vector3.Transform(Vector3.UnitZ, r).Z, Eps, "tilt kept");
            }

            Assert.AreEqual(HeadTilt, PinnacleFacing.FaceToward(HeadTilt, head, head), "no turn onto itself");
        }

        [TestMethod]
        public void FaceToward_OffsetZero_IsUnchanged()
        {
            var head = new Vector2(10f, -20f);
            var target = new Vector2(32.5f, -5f);

            Assert.AreEqual(PinnacleFacing.FaceToward(HeadTilt, head, target), PinnacleFacing.FaceToward(HeadTilt, head, target, 0f));
        }

        [TestMethod]
        public void FaceToward_OffsetPi_PointsTheOppositeLocalAxisAtTheTarget()
        {
            var head = new Vector2(10f, -20f);

            foreach (var target in new[] { new Vector2(32.5f, -20f), new Vector2(10f, 0f), new Vector2(0f, -30f), new Vector2(-4f, 9f) })
            {
                var toTarget = Vector2.Normalize(target - head);

                // the model face is on local -Y, so with offset pi the rotated -Y axis points at the target
                var face = Vector3.Transform(-Vector3.UnitY, PinnacleFacing.FaceToward(HeadTilt, head, target, (float)Math.PI));
                var flat = Vector2.Normalize(new Vector2(face.X, face.Y));

                Assert.AreEqual(toTarget.X, flat.X, Eps, $"x toward {target}");
                Assert.AreEqual(toTarget.Y, flat.Y, Eps, $"y toward {target}");

                // and with offset 0 the same -Y axis points away (the playtest bug)
                var away = Vector3.Transform(-Vector3.UnitY, PinnacleFacing.FaceToward(HeadTilt, head, target));
                var awayFlat = Vector2.Normalize(new Vector2(away.X, away.Y));
                Assert.IsTrue(Vector2.Dot(awayFlat, toTarget) < -0.99f, "offset 0 faces away");
            }
        }

        // ---- the client-animated turn (about the body's local up axis) ----

        private static Vector2 Flat(Vector3 v) => Vector2.Normalize(new Vector2(v.X, v.Y));

        /// <summary>An upright body: the local turn is the plain yaw turn, so it lands exactly where FaceToward does.</summary>
        [TestMethod]
        public void LocalTurnToward_UprightBody_MatchesFaceToward()
        {
            var start = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.3f);
            var head = new Vector2(10f, -20f);

            foreach (var target in new[] { new Vector2(32.5f, -20f), new Vector2(10f, 0f), new Vector2(0f, -30f), new Vector2(-4f, -20.5f) })
            {
                var angle = PinnacleFacing.LocalTurnToward(start, head, target, (float)Math.PI);
                var turned = PinnacleFacing.RotateAboutLocalUp(start, angle);
                var faced = PinnacleFacing.FaceToward(start, head, target, (float)Math.PI);

                Assert.IsTrue(Math.Abs(Quaternion.Dot(turned, faced)) > 0.99999f, $"toward {target}: {turned} vs {faced}");
                Assert.IsTrue(Math.Abs(angle) <= MathF.PI + Eps, "the short way round");
            }
        }

        /// <summary>
        /// The Grael head is tilted (its up axis leans 9.4 degrees off vertical). Turned about that axis, as the client
        /// turns it, the face (local -Y, offset pi) ends pointing horizontally at the target, and the up axis has not moved.
        /// </summary>
        [TestMethod]
        public void LocalTurnToward_TiltedHead_FacesTheTarget_AndKeepsItsUpAxis()
        {
            var head = new Vector2(10.6544f, -20.161f);
            var up = Vector3.Transform(Vector3.UnitZ, HeadTilt);

            Assert.IsTrue(up.Z < 0.99f, $"the fixture really is tilted ({up})");

            foreach (var target in new[] { new Vector2(32.5f, -20f), new Vector2(20f, -30f), new Vector2(20f, -10f), new Vector2(10.6544f, 0f), new Vector2(-2f, -20f), new Vector2(0f, -33f) })
            {
                var angle = PinnacleFacing.LocalTurnToward(HeadTilt, head, target, (float)Math.PI);
                var turned = PinnacleFacing.RotateAboutLocalUp(HeadTilt, angle);

                var face = Flat(Vector3.Transform(-Vector3.UnitY, turned));
                var toTarget = Vector2.Normalize(target - head);

                Assert.AreEqual(toTarget.X, face.X, Eps, $"x toward {target}");
                Assert.AreEqual(toTarget.Y, face.Y, Eps, $"y toward {target}");
                AssertVec(up, Vector3.Transform(Vector3.UnitZ, turned), $"up axis fixed toward {target}");
                Assert.AreEqual(1f, turned.Length(), Eps);
            }

            Assert.AreEqual(0f, PinnacleFacing.LocalTurnToward(HeadTilt, head, head, (float)Math.PI), "no turn onto itself");
        }

        /// <summary>Already facing: no turn. Turning twice toward the same target turns only once.</summary>
        [TestMethod]
        public void LocalTurnToward_AlreadyFacing_IsZero()
        {
            var head = new Vector2(10.6544f, -20.161f);
            var target = new Vector2(20f, -30f);

            var turned = PinnacleFacing.RotateAboutLocalUp(HeadTilt, PinnacleFacing.LocalTurnToward(HeadTilt, head, target, (float)Math.PI));

            Assert.AreEqual(0f, PinnacleFacing.LocalTurnToward(turned, head, target, (float)Math.PI), Eps);
        }

        /// <summary>
        /// The spawn rotation faces east (the face, local -Y, points +X). A target north-east is a left (counter-clockwise,
        /// positive) turn of 45 degrees, one south-east a right turn, one due west half a turn.
        /// </summary>
        [TestMethod]
        public void LocalTurnToward_SignAndSize()
        {
            var head = Vector2.Zero;

            Assert.AreEqual(MathF.PI / 4, PinnacleFacing.LocalTurnToward(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2), head, new Vector2(1f, 1f), (float)Math.PI), Eps, "north-east: left 45");
            Assert.AreEqual(-MathF.PI / 4, PinnacleFacing.LocalTurnToward(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2), head, new Vector2(1f, -1f), (float)Math.PI), Eps, "south-east: right 45");
            Assert.AreEqual(MathF.PI, Math.Abs(PinnacleFacing.LocalTurnToward(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2), head, new Vector2(-1f, 0f), (float)Math.PI)), Eps, "west: half a turn");
        }

        /// <summary>
        /// The command speed: the client turns at omega x speed, and a TurnRight omega is clockwise (negative), so a
        /// counter-clockwise turn needs a negative speed.
        /// </summary>
        [TestMethod]
        public void TurnCommandSpeed_SignFollowsTheAngle()
        {
            const float omega = -3.5f; // 0x0900016A's TurnRight cycle

            Assert.AreEqual(-0.5f, PinnacleFacing.TurnCommandSpeed(0.7f, omega, 0.5f), "counter-clockwise: negative TurnRight speed");
            Assert.AreEqual(0.5f, PinnacleFacing.TurnCommandSpeed(-0.7f, omega, 0.5f), "clockwise: positive");
            Assert.AreEqual(0.5f, PinnacleFacing.TurnCommandSpeed(0.7f, 3.5f, 0.5f), "a table whose TurnRight were counter-clockwise flips it back");

            // the client's rate is omega x speed: its sign must be the angle's
            Assert.IsTrue(omega * PinnacleFacing.TurnCommandSpeed(0.7f, omega, 0.5f) > 0f);
            Assert.IsTrue(omega * PinnacleFacing.TurnCommandSpeed(-0.7f, omega, 0.5f) < 0f);
        }

        /// <summary>
        /// The client turns in whole animation-frame steps of omega / framerate (0x0900016A: 3.5 / 30 rad, 6.7 degrees),
        /// one every 1 / (framerate x speed) seconds. The stop goes half a frame after the last wanted step; the client
        /// lands on the nearest whole number of steps; under half a step there is no turn at all.
        /// </summary>
        [TestMethod]
        public void TurnSteps_TurnSeconds_AndTurnLanding()
        {
            const float omega = -3.5f;
            const float framerate = 30f;
            var step = 3.5 / 30.0;

            Assert.AreEqual(step, PinnacleFacing.TurnStep(omega, framerate), 1e-9);
            Assert.AreEqual(step, PinnacleFacing.TurnStep(-omega, -framerate), 1e-9, "magnitudes");
            Assert.AreEqual(0.0, PinnacleFacing.TurnStep(omega, 0f), "no frames: a continuous turn");

            Assert.AreEqual(8, PinnacleFacing.TurnSteps(0.93f, omega, framerate), "0.93 / 0.1167 = 7.97 -> 8");
            Assert.AreEqual(8, PinnacleFacing.TurnSteps(-0.93f, omega, framerate), "either direction");
            Assert.AreEqual(0, PinnacleFacing.TurnSteps(0.05f, omega, framerate), "under half a step: none");
            Assert.AreEqual(1, PinnacleFacing.TurnSteps(0.06f, omega, framerate), "just over half a step: one");

            Assert.AreEqual(8.5 / 30.0, PinnacleFacing.TurnSeconds(0.93f, omega, framerate, 1f), 1e-9, "speed 1: 30 steps a second, the stop half a frame after the 8th");
            Assert.AreEqual(8.5 / 15.0, PinnacleFacing.TurnSeconds(0.93f, omega, framerate, 0.5f), 1e-9, "speed 0.5: 15 steps a second");
            Assert.AreEqual(0.0, PinnacleFacing.TurnSeconds(0.05f, omega, framerate, 1f), "no step, no wait");
            Assert.AreEqual(0.0, PinnacleFacing.TurnSeconds(1f, 0f, framerate, 1f), "no turn omega: nothing to wait for");
            Assert.AreEqual(0.5, PinnacleFacing.TurnSeconds(-1.75f, omega, 0f, 1f), 1e-6, "continuous: the angle over the rate");

            Assert.AreEqual((float)(8 * step), PinnacleFacing.TurnLanding(0.93f, omega, framerate), 1e-6f);
            Assert.AreEqual((float)(-8 * step), PinnacleFacing.TurnLanding(-0.93f, omega, framerate), 1e-6f);
            Assert.AreEqual(0f, PinnacleFacing.TurnLanding(0.05f, omega, framerate));
            Assert.AreEqual(0.4f, PinnacleFacing.TurnLanding(0.4f, omega, 0f), "continuous: exact");

            foreach (var a in new[] { 0.2f, 0.93f, -1.3f, 2.9f, -3.1f })
                Assert.IsTrue(Math.Abs(PinnacleFacing.TurnLanding(a, omega, framerate) - a) <= step / 2 + 1e-6, $"within half a step of {a}");
        }

        [TestMethod]
        public void PartWay_InterpolatesAboutTheLocalUpAxis_AndClamps()
        {
            var full = PinnacleFacing.RotateAboutLocalUp(HeadTilt, 1.2f);

            Assert.IsTrue(Math.Abs(Quaternion.Dot(PinnacleFacing.PartWay(HeadTilt, 1.2f, 0.5), PinnacleFacing.RotateAboutLocalUp(HeadTilt, 0.6f))) > 0.99999f, "half way");
            Assert.IsTrue(Math.Abs(Quaternion.Dot(PinnacleFacing.PartWay(HeadTilt, 1.2f, 3.0), full)) > 0.99999f, "past the end: the end");
            Assert.IsTrue(Math.Abs(Quaternion.Dot(PinnacleFacing.PartWay(HeadTilt, 1.2f, -1.0), HeadTilt)) > 0.99999f, "before the start: the start");
        }

        // ---- PinnacleHeadTurns: the turn and nod bookkeeping LivePinnacleWorld drives (review 2026-10-20, F6) ----

        private const float Omega = -3.5f;      // 0x0900016A's TurnRight cycle
        private const float Framerate = 30f;
        private static readonly Vector2 HeadXY = new Vector2(10.6544f, -20.161f);
        private static readonly Vector2 SouthEast = new Vector2(20f, -30f);
        private static readonly Vector2 NorthEast = new Vector2(20f, -10f);

        private static bool Same(Quaternion a, Quaternion b) => Math.Abs(Quaternion.Dot(a, b)) > 0.99999f;

        private static PinnacleHeadTurns.TurnPlan Turn(PinnacleHeadTurns turns, Vector2 owner, bool alive = true, float omega = Omega)
            => turns.BeginTurn(1, HeadTilt, alive, HeadXY, owner, (float)Math.PI, omega, Framerate, 1f);

        [TestMethod]
        public void HeadTurns_Turn_BooksWhereTheClientLands_AndTakesItsOwnStop()
        {
            var now = 100.0;
            var turns = new PinnacleHeadTurns(() => now);

            var plan = Turn(turns, SouthEast);

            Assert.AreEqual(PinnacleHeadTurns.TurnKind.Turn, plan.Kind);
            Assert.AreEqual(1, plan.Generation);

            var angle = PinnacleFacing.LocalTurnToward(HeadTilt, HeadXY, SouthEast, (float)Math.PI);
            Assert.IsTrue(Same(PinnacleFacing.RotateAboutLocalUp(HeadTilt, PinnacleFacing.TurnLanding(angle, Omega, Framerate)), plan.Target), "booked at the landing");
            Assert.IsTrue(Same(plan.Target, turns.Booked(1, HeadTilt)));
            Assert.AreEqual(PinnacleFacing.TurnCommandSpeed(angle, Omega, 1f), plan.CommandSpeed);
            Assert.AreEqual(PinnacleFacing.TurnSeconds(angle, Omega, Framerate, 1f), plan.Seconds, 1e-9);
            Assert.IsNull(plan.RestoreRest);
            Assert.IsTrue(turns.InFlight(1));

            Assert.IsTrue(Same(HeadTilt, turns.Current(1, HeadTilt)), "at the start the client still shows the start");
            now += plan.Seconds;
            Assert.IsTrue(Same(plan.Target, turns.Current(1, HeadTilt)), "at the end, the landing");

            Assert.IsTrue(turns.TakeStop(1, plan.Generation, alive: true), "its own stop is sent");
            Assert.IsFalse(turns.InFlight(1));
            Assert.IsFalse(turns.TakeStop(1, plan.Generation, alive: true), "once");
        }

        /// <summary>A second FaceOwner half way through a turn starts from where the first had got, and the first one's stop is stale.</summary>
        [TestMethod]
        public void HeadTurns_OverlappingTurn_StartsMidTurn_AndDropsTheStaleStop()
        {
            var now = 0.0;
            var turns = new PinnacleHeadTurns(() => now);

            var first = Turn(turns, SouthEast);
            now = first.Seconds / 2;
            var mid = turns.Current(1, HeadTilt);

            Assert.IsFalse(Same(mid, HeadTilt) || Same(mid, first.Target), "really part way");

            var second = Turn(turns, NorthEast);

            Assert.AreEqual(PinnacleHeadTurns.TurnKind.Turn, second.Kind);
            Assert.AreEqual(2, second.Generation);

            var angle = PinnacleFacing.LocalTurnToward(mid, HeadXY, NorthEast, (float)Math.PI);
            Assert.IsTrue(Same(PinnacleFacing.RotateAboutLocalUp(mid, PinnacleFacing.TurnLanding(angle, Omega, Framerate)), second.Target), "the second turn starts from the mid-turn rotation");

            Assert.IsFalse(turns.TakeStop(1, first.Generation, alive: true), "the first turn's stop is stale: it would stop the second");
            Assert.IsTrue(turns.InFlight(1), "and leaves the second in flight");
            Assert.IsTrue(turns.TakeStop(1, second.Generation, alive: true));
        }

        [TestMethod]
        public void HeadTurns_Nod_WaitsOutTheTurnPlusSettle_AndANewerTurnMakesItsFramesStale()
        {
            var now = 0.0;
            var turns = new PinnacleHeadTurns(() => now);

            var turn = Turn(turns, SouthEast);
            now = 0.1;
            var nod = turns.BeginNod(1, HeadTilt, alive: true);

            Assert.IsFalse(nod.Refused);
            Assert.AreEqual(turn.Generation, nod.Generation);
            Assert.IsTrue(Same(turn.Target, nod.Rest), "the nod pitches from the booked rotation");
            Assert.AreEqual(turn.Seconds - 0.1 + PinnacleHeadTurns.SettleSeconds, nod.DelaySeconds, 1e-9, "the rest of the turn plus the settle");

            Assert.IsTrue(turns.TakeStop(1, turn.Generation, alive: true));
            Assert.AreEqual(0.0, turns.BeginNod(1, HeadTilt, alive: true).DelaySeconds, "no turn in flight: at once");

            var newer = Turn(turns, NorthEast);
            Assert.IsFalse(turns.TakeNodFrame(1, nod.Generation, alive: true, isRest: false), "a frame of the older nod is stale");
            Assert.IsFalse(turns.TakeNodFrame(1, nod.Generation, alive: true, isRest: true), "its rest frame too");
            Assert.IsTrue(turns.TakeStop(1, newer.Generation, alive: true));
        }

        /// <summary>
        /// A nod cut off after a pitched frame (its rest frame never drawn) leaves the head pitched; the next turn is told
        /// to restore the rest rotation first and turns from it. Cut off before any frame, or after its rest frame, nothing
        /// to restore.
        /// </summary>
        [TestMethod]
        public void HeadTurns_SupersededPitchedNod_RestoresItsRest()
        {
            var now = 0.0;
            var turns = new PinnacleHeadTurns(() => now);

            var turn = Turn(turns, SouthEast);
            Assert.IsTrue(turns.TakeStop(1, turn.Generation, alive: true));

            // before any frame: nothing drawn, nothing to restore
            turns.BeginNod(1, HeadTilt, alive: true);
            var unpitched = Turn(turns, SouthEast);
            Assert.IsNull(unpitched.RestoreRest);

            // a pitched frame drawn, the rest frame not: restore
            var nod = turns.BeginNod(1, HeadTilt, alive: true);
            Assert.IsTrue(turns.TakeNodFrame(1, nod.Generation, alive: true, isRest: false));
            var cut = Turn(turns, NorthEast);
            Assert.IsTrue(cut.RestoreRest.HasValue, "the pitched head is put back first");
            Assert.IsTrue(Same(nod.Rest, cut.RestoreRest.Value));
            var angle = PinnacleFacing.LocalTurnToward(nod.Rest, HeadXY, NorthEast, (float)Math.PI);
            Assert.IsTrue(Same(PinnacleFacing.RotateAboutLocalUp(nod.Rest, PinnacleFacing.TurnLanding(angle, Omega, Framerate)), cut.Target), "and the turn starts from the rest rotation");
            Assert.IsTrue(turns.TakeStop(1, cut.Generation, alive: true));

            // a whole nod (rest frame drawn): nothing to restore
            var whole = turns.BeginNod(1, HeadTilt, alive: true);
            Assert.IsTrue(turns.TakeNodFrame(1, whole.Generation, alive: true, isRest: false));
            Assert.IsTrue(turns.TakeNodFrame(1, whole.Generation, alive: true, isRest: true));
            Assert.IsFalse(turns.TakeNodFrame(1, whole.Generation, alive: true, isRest: false), "a frame after the nod's rest frame is stale: the nod is over, same generation or not");
            Assert.IsNull(Turn(turns, SouthEast).RestoreRest);
        }

        /// <summary>A dead head (lingering through its death animation) is neither turned nor nodded, and a turn in flight is closed without a stop.</summary>
        [TestMethod]
        public void HeadTurns_DeadHead_Refuses()
        {
            var now = 0.0;
            var turns = new PinnacleHeadTurns(() => now);

            var turn = Turn(turns, SouthEast);
            var nod = turns.BeginNod(1, HeadTilt, alive: true);

            var refused = Turn(turns, NorthEast, alive: false);
            Assert.AreEqual(PinnacleHeadTurns.TurnKind.Refused, refused.Kind);
            Assert.AreEqual(turn.Generation, turns.Generation(1), "no new generation");
            Assert.IsTrue(Same(turn.Target, turns.Booked(1, HeadTilt)), "nothing re-booked");

            Assert.IsTrue(turns.BeginNod(1, HeadTilt, alive: false).Refused);
            Assert.IsFalse(turns.TakeNodFrame(1, nod.Generation, alive: false, isRest: false), "no nod frame over the death pose");
            Assert.IsFalse(turns.TakeStop(1, turn.Generation, alive: false), "no Ready over the death motion");
            Assert.IsFalse(turns.InFlight(1), "but the turn is closed");
        }

        [TestMethod]
        public void HeadTurns_NoMotionTableTurn_Snaps_AndAlreadyFacingHolds()
        {
            var now = 0.0;
            var turns = new PinnacleHeadTurns(() => now);

            var snap = Turn(turns, SouthEast, omega: 0f);
            Assert.AreEqual(PinnacleHeadTurns.TurnKind.Snap, snap.Kind);
            Assert.IsTrue(Same(PinnacleFacing.FaceToward(HeadTilt, HeadXY, SouthEast, (float)Math.PI), snap.Target), "the FaceToward fallback");
            Assert.IsFalse(turns.InFlight(1));

            var fresh = new PinnacleHeadTurns(() => now);
            var turn = fresh.BeginTurn(1, HeadTilt, true, HeadXY, SouthEast, (float)Math.PI, Omega, Framerate, 1f);

            // the same owner again at the moment the turn lands, before its stop fired: the rest is under half a step
            now = turn.Seconds;
            var hold = fresh.BeginTurn(1, HeadTilt, true, HeadXY, SouthEast, (float)Math.PI, Omega, Framerate, 1f);
            Assert.AreEqual(PinnacleHeadTurns.TurnKind.Hold, hold.Kind, "already facing as nearly as the client can turn");
            Assert.IsTrue(hold.StopInFlight, "the first turn was still in flight (its stop not yet taken): stop it");
            Assert.IsTrue(Same(turn.Target, hold.Target));
            Assert.IsFalse(fresh.InFlight(1));
        }

        [TestMethod]
        public void NodPitchSign_FlipsOnlyForAFaceOnTheFarSide()
        {
            Assert.AreEqual(1f, LivePinnacleWorld.NodPitchSign(0f));
            Assert.AreEqual(-1f, LivePinnacleWorld.NodPitchSign((float)Math.PI));
        }
    }
}
