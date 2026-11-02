using System;
using System.Numerics;

namespace ACE.Server.Pinnacles
{
    /// <summary>
    /// The head's turn-to-face before each cast (section 7 Q4), pure. ACE's heading convention: the identity rotation
    /// faces +Y (north), and a heading is a rotation about +Z by atan2(-dx, dy) (Position.Rotate,
    /// WorldObject_Magic projectile facing), so east (+X) is -pi/2 and west is +pi/2.
    ///
    /// The tilt is preserved by rotating the WHOLE spawn rotation about the world Z axis: rotation = yaw * base
    /// (System.Numerics applies the right operand first), which leaves the angle between the object's up axis and
    /// world Z unchanged.
    /// </summary>
    public static class PinnacleFacing
    {
        /// <summary>The heading (radians, about +Z) that faces from <paramref name="from"/> toward <paramref name="to"/>; 0 when they coincide.</summary>
        public static float YawToward(Vector2 from, Vector2 to)
        {
            var d = to - from;

            if (d.LengthSquared() < 1e-8f)
                return 0f;

            return (float)Math.Atan2(-d.X, d.Y);
        }

        /// <summary>A world-Z rotation by <paramref name="yaw"/> applied after <paramref name="baseTilt"/>; normalized.</summary>
        public static Quaternion ComposeFacing(float yaw, Quaternion baseTilt)
            => Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, yaw) * baseTilt);

        /// <summary>The heading of <paramref name="rotation"/>'s local +Y axis projected onto the ground plane; 0 when it points straight up or down.</summary>
        public static float YawOf(Quaternion rotation)
        {
            var forward = Vector3.Transform(Vector3.UnitY, rotation);

            if (forward.X * forward.X + forward.Y * forward.Y < 1e-8f)
                return 0f;

            return (float)Math.Atan2(-forward.X, forward.Y);
        }

        /// <summary>
        /// The rotation that turns <paramref name="spawnRotation"/> about world Z until its local +Y axis faces
        /// <paramref name="to"/>, keeping its tilt. <paramref name="spawnRotation"/> unchanged when the points coincide.
        /// Which local axis a model's face is on is a content fact checked in game [?]; a constant model offset is a
        /// yaw passed as <paramref name="faceYawOffset"/> (radians, default 0 = unchanged): the Grael head's content
        /// rotation points its face along the opposite local axis, so its script data carries pi.
        /// </summary>
        public static Quaternion FaceToward(Quaternion spawnRotation, Vector2 from, Vector2 to, float faceYawOffset = 0f)
        {
            if ((to - from).LengthSquared() < 1e-8f)
                return spawnRotation;

            return ComposeFacing(YawToward(from, to) - YawOf(spawnRotation) + faceYawOffset, spawnRotation);
        }

        // ================================================================ the client-animated turn

        /// <summary>
        /// The turn a CLIENT plays for a motion-table turn command rotates the body about its OWN up axis (local +Z),
        /// not about world Z: the ported client physics adds the animation's omega to an identity offset frame
        /// (Sequence.apply_physics) and composes it onto the body as Position * offset (PhysicsObj.UpdatePositionInternal,
        /// AFrame.Combine). This is that rotation: <paramref name="angle"/> radians about <paramref name="rotation"/>'s
        /// local +Z (positive = counter-clockwise seen from above the body), normalized.
        /// </summary>
        public static Quaternion RotateAboutLocalUp(Quaternion rotation, float angle)
            => Quaternion.Normalize(rotation * Quaternion.CreateFromAxisAngle(Vector3.UnitZ, angle));

        /// <summary>
        /// The signed angle (radians, in [-pi, pi], about the body's local +Z as <see cref="RotateAboutLocalUp"/> turns
        /// it) that brings the face of <paramref name="current"/> - local +Y turned by <paramref name="faceYawOffset"/>
        /// about local +Z, the same face axis <see cref="FaceToward"/> uses - to point horizontally at
        /// <paramref name="to"/>. A tilted body keeps its up axis fixed in the world while it turns this way, so the
        /// target face is the one on the circle perpendicular to that up axis that lies in the vertical plane through
        /// <paramref name="to"/>, on the target's side. 0 when the points coincide.
        /// </summary>
        public static float LocalTurnToward(Quaternion current, Vector2 from, Vector2 to, float faceYawOffset = 0f)
        {
            var flat = to - from;

            if (flat.LengthSquared() < 1e-8f)
                return 0f;

            flat = Vector2.Normalize(flat);
            var toward = new Vector3(flat.X, flat.Y, 0f);

            var up = Vector3.Transform(Vector3.UnitZ, current);
            var face = Vector3.Transform(Vector3.Transform(Vector3.UnitY, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, faceYawOffset)), current);

            // perpendicular to the up axis AND to the vertical plane's normal: the face that lies in that plane
            var planeNormal = Vector3.Cross(toward, Vector3.UnitZ);
            var target = Vector3.Cross(up, planeNormal);

            if (target.LengthSquared() < 1e-12f)
                return 0f;

            target = Vector3.Normalize(target);

            if (Vector3.Dot(target, toward) < 0f)
                target = -target;

            return (float)Math.Atan2(Vector3.Dot(up, Vector3.Cross(face, target)), Vector3.Dot(face, target));
        }

        /// <summary>
        /// The TurnSpeed to send with a TurnRight command so the client turns by the sign of <paramref name="angle"/>:
        /// the client's rate is the motion table's turn omega times this speed (MotionTable.add_motion), and a TurnRight
        /// omega is clockwise (negative about +Z) in every retail table checked, so a counter-clockwise turn needs a
        /// negative speed - the same signed TurnRight the server already relays for a player's left turn
        /// (MovementData's MoveToState conversion). <paramref name="speed"/> is the magnitude (&gt; 0).
        /// </summary>
        public static float TurnCommandSpeed(float angle, float omegaZ, float speed)
            => Math.Sign(angle) * Math.Sign(omegaZ) * Math.Abs(speed);

        /// <summary>
        /// The angle (radians) the client turns per animation frame of a turn cycle: the ported client applies a cycle's
        /// omega once per frame the animation advances, for 1/framerate seconds each (Sequence.update_internal), so a
        /// turn moves in whole steps of |omega| / framerate whatever the speed (speed scales omega and framerate alike).
        /// 0 for a turn with no animation frames (framerate 0), which the client applies continuously.
        /// </summary>
        public static double TurnStep(float omegaZ, float framerate)
            => Math.Abs(framerate) < 1e-6f ? 0.0 : Math.Abs((double)omegaZ) / Math.Abs(framerate);

        /// <summary>
        /// The number of steps (<see cref="TurnStep"/>) the client should turn to come nearest to <paramref name="angle"/>
        /// (0 when the angle is under half a step); 0 for a continuous turn.
        /// </summary>
        public static int TurnSteps(float angle, float omegaZ, float framerate)
        {
            var step = TurnStep(omegaZ, framerate);

            return step <= 0.0 ? 0 : (int)Math.Round(Math.Abs(angle) / step, MidpointRounding.AwayFromZero);
        }

        /// <summary>
        /// When (seconds after the start motion) to send the stop. A stepped turn (<see cref="TurnStep"/>) takes its k-th
        /// step k / (framerate x speed) seconds in, so the stop is sent half a frame after the last wanted step: the client
        /// then turns exactly <see cref="TurnSteps"/> steps even if the stop arrives up to half a frame early or late. A
        /// continuous turn stops at angle / rate. 0 when the table cannot turn.
        /// </summary>
        public static double TurnSeconds(float angle, float omegaZ, float framerate, float speed)
        {
            var rate = Math.Abs((double)omegaZ * speed);

            if (rate < 1e-6)
                return 0.0;

            var steps = TurnSteps(angle, omegaZ, framerate);

            if (steps == 0)
                return Math.Abs(framerate) < 1e-6f ? Math.Abs(angle) / rate : 0.0;

            return (steps + 0.5) / (Math.Abs(framerate) * Math.Abs(speed));
        }

        /// <summary>The signed angle the client actually turns: <see cref="TurnSteps"/> whole steps in <paramref name="angle"/>'s direction (the angle itself for a continuous turn).</summary>
        public static float TurnLanding(float angle, float omegaZ, float framerate)
        {
            var step = TurnStep(omegaZ, framerate);

            return step <= 0.0 ? angle : (float)(Math.Sign(angle) * TurnSteps(angle, omegaZ, framerate) * step);
        }

        /// <summary>
        /// <paramref name="from"/> turned by <paramref name="fraction"/> of <paramref name="angle"/> about its local up
        /// axis: where a turn in flight has got to (fraction clamped to [0, 1]).
        /// </summary>
        public static Quaternion PartWay(Quaternion from, float angle, double fraction)
            => RotateAboutLocalUp(from, (float)(angle * Math.Clamp(fraction, 0.0, 1.0)));
    }
}
