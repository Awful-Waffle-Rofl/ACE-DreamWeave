using System;
using System.Collections.Generic;
using System.Numerics;

using Position = ACE.Entity.Position;

namespace ACE.Server.MlDigsite
{
    /// <summary>
    /// THE one place a Boss Rush mechanic does position arithmetic, and the only geometry
    /// ACE.Server.WorldEvents.WorldEventGeometry does not already give it.
    ///
    /// WorldEventGeometry.Ring and .Disc cover every circular shape here and are used directly by the
    /// modules; what it has no equivalent of is a LINE, which the drum cadence's two-beat "wall" needs, and a
    /// compass bearing, which the interrupt object's chat line needs so a player can be told where to run.
    ///
    /// Pure and landblock-free, the same contract WorldEventGeometry states: nothing here touches the
    /// physics engine or the terrain. Terrain snapping happens at placement time, in MlDigsiteProps.
    /// Every returned Position is NEW - the centre is never mutated - carries the centre's Instance, and has
    /// had SetPosition applied, so an offset crossing a landblock boundary arrives addressed to the block it
    /// actually landed in.
    /// </summary>
    public static class MlDigsiteMechanicGeometry
    {
        /// <summary>
        /// <paramref name="points"/> positions evenly spaced along the line through <paramref name="centre"/>
        /// in the direction <paramref name="heading"/>, spanning <paramref name="length"/> metres end to end
        /// and centred on the centre. One point returns the centre itself.
        ///
        /// Used to DRAW the wall, never to decide who it hits - that is
        /// MlDigsiteBossMechanicRules.InsideWall, which measures a real perpendicular distance rather than a
        /// distance to the nearest drawn marker, so a player standing between two markers is still hit.
        /// </summary>
        public static IReadOnlyList<Position> Line(Position centre, Vector3 heading, float length, int points)
        {
            var result = new List<Position>();

            if (centre == null || points < 1)
                return result;

            var magnitude = Math.Sqrt(heading.X * heading.X + heading.Y * heading.Y);

            if (!double.IsFinite(magnitude) || magnitude <= 0.0)
                return result;

            var ux = heading.X / magnitude;
            var uy = heading.Y / magnitude;

            if (points == 1)
            {
                result.Add(new Position(centre));
                return result;
            }

            var step = length / (points - 1);

            for (var i = 0; i < points; i++)
            {
                var t = -length * 0.5 + step * i;

                result.Add(Offset(centre, (float)(ux * t), (float)(uy * t)));
            }

            return result;
        }

        /// <summary>
        /// A new position <paramref name="dx"/>/<paramref name="dy"/> metres from <paramref name="origin"/>,
        /// keeping its facing and its instance. SetPosition recomputes the landblock and the land cell from
        /// the new X/Y, so an offset that crosses a block boundary comes back addressed to the block it landed
        /// in - the same reason WorldEventGeometry does it this way.
        /// </summary>
        public static Position Offset(Position origin, float dx, float dy)
        {
            var placed = new Position(origin.LandblockId.Raw,
                origin.PositionX, origin.PositionY, origin.PositionZ,
                origin.RotationX, origin.RotationY, origin.RotationZ, origin.RotationW,
                origin.Instance);

            placed.SetPosition(new Vector3(origin.PositionX + dx, origin.PositionY + dy, origin.PositionZ));

            return placed;
        }

        /// <summary>
        /// The 8-point compass name for an offset, for a chat line that has to tell a player which way to
        /// run. AC's world frame is +Y north and +X east (the same convention PositionExtensions' map
        /// coordinates use), so the bearing is measured clockwise from +Y.
        ///
        /// A zero-length offset reads "nearby" rather than picking an arbitrary direction.
        /// </summary>
        public static string Compass(double dx, double dy)
        {
            if (!double.IsFinite(dx) || !double.IsFinite(dy))
                return "nearby";

            if (Math.Abs(dx) < 0.001 && Math.Abs(dy) < 0.001)
                return "nearby";

            var degrees = Math.Atan2(dx, dy) * 180.0 / Math.PI;

            if (degrees < 0)
                degrees += 360.0;

            var octant = (int)Math.Round(degrees / 45.0) % 8;

            switch (octant)
            {
                case 0: return "north";
                case 1: return "northeast";
                case 2: return "east";
                case 3: return "southeast";
                case 4: return "south";
                case 5: return "southwest";
                case 6: return "west";
                default: return "northwest";
            }
        }

        /// <summary>
        /// The distance from <paramref name="point"/> to the NEAREST of <paramref name="marks"/>, or
        /// double.MaxValue when there are none - which makes "outside every safe zone" the answer when no
        /// safe zone was placed, and the caller decides whether that should hit anybody.
        ///
        /// Instance-aware, like every other distance test in this system: a mark in another realm copy of the
        /// same landblock is not near anybody.
        /// </summary>
        public static double NearestDistance(Position point, IReadOnlyList<Position> marks)
        {
            if (point == null || marks == null || marks.Count == 0)
                return double.MaxValue;

            var best = double.MaxValue;

            for (var i = 0; i < marks.Count; i++)
            {
                var mark = marks[i];

                if (mark == null || mark.Instance != point.Instance)
                    continue;

                var distance = point.DistanceTo(mark);

                if (distance < best)
                    best = distance;
            }

            return best;
        }
    }
}
