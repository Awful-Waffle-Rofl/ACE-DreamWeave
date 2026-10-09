using System;
using System.Numerics;

using ACE.Server.Entity;

namespace ACE.Server.PuzzleGates
{
    /// <summary>
    /// Pure beam aiming. A Puzzle Light's effect runs along its host's local -Z (axis=Down, the default) or
    /// +Z (axis=Up). <see cref="SkyDecorLayout.Orientation(float, float, bool, out float, out float, out float, out float)"/>
    /// with mast:true builds qz(yaw)*qx(tilt), which maps local +Z to
    /// (sin(tilt)sin(yaw), -sin(tilt)cos(yaw), cos(tilt)) (<see cref="SkyDecorLayout.DiscNormal(float, float, bool)"/>).
    /// Solving that for a wanted beam direction d gives the tilt and yaw below.
    /// </summary>
    public static class PuzzleBeamAim
    {
        /// <summary>Tilt and yaw (degrees) whose mast orientation points the host's beam axis along <paramref name="d"/>.</summary>
        public static void TiltYaw(Vector3 d, PuzzleBeamAxis axis, out float yawDeg, out float tiltDeg)
        {
            d = Vector3.Normalize(d);

            // Down: local -Z must map to d, so local +Z maps to -d. Up: local +Z maps to d.
            var n = axis == PuzzleBeamAxis.Down ? -d : d;

            var cos = Math.Clamp(n.Z, -1.0f, 1.0f);
            var tilt = Math.Acos(cos);

            // At tilt 0 or 180 yaw is irrelevant to the axis; pin it to 0 rather than atan2(0, 0) noise.
            var horizontal = Math.Sqrt((double)n.X * n.X + (double)n.Y * n.Y);
            var yaw = horizontal < 1e-6 ? 0.0 : Math.Atan2(n.X, -n.Y);

            tiltDeg = (float)(tilt * 180.0 / Math.PI);
            yawDeg = (float)(yaw * 180.0 / Math.PI);
        }

        /// <summary>The host orientation that points its beam axis along <paramref name="d"/>.</summary>
        public static Quaternion Aim(Vector3 d, PuzzleBeamAxis axis)
        {
            TiltYaw(d, axis, out var yawDeg, out var tiltDeg);
            SkyDecorLayout.Orientation(yawDeg, tiltDeg, true, out var w, out var x, out var y, out var z);
            return new Quaternion(x, y, z, w);
        }

        /// <summary>The world direction a host with orientation <paramref name="q"/> beams along.</summary>
        public static Vector3 BeamDirection(Quaternion q, PuzzleBeamAxis axis)
        {
            var local = axis == PuzzleBeamAxis.Down ? new Vector3(0, 0, -1) : new Vector3(0, 0, 1);
            return Vector3.Transform(local, q);
        }

        /// <summary>
        /// Host position for a beam hitting <paramref name="target"/>: min(length, distance to hub) from the
        /// target, toward the hub.
        /// </summary>
        public static Vector3 HostPosition(Vector3 target, Vector3 hub, float length)
        {
            var toHub = hub - target;
            var dist = toHub.Length();

            if (dist < 1e-6f)
                return target;

            return target + Vector3.Normalize(toHub) * Math.Min(length, dist);
        }
    }
}