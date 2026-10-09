using System;
using System.Collections.Generic;
using System.Numerics;

using ACE.Common;
using ACE.Entity;

namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// Pure spawn-position arithmetic for the three source-theme geometries (TECH-DESIGN 2.4, 5.6).
    ///
    /// Nothing here touches a landblock, the physics engine or the terrain: every method is a coordinate
    /// transform over a centre <see cref="Position"/>, which is what makes the whole shape unit testable
    /// without standing up a world (D6). Terrain snapping happens at spawn time in
    /// <see cref="WorldEventSpawner"/>, not here.
    ///
    /// Every returned position is a NEW Position - the centre is never mutated - carries the centre's
    /// Instance, and has had SetPosition applied, so an outdoor offset that crosses a landblock boundary
    /// arrives with a consistent landblock/cell. Z is always the centre's Z.
    ///
    /// The four-argument entry points draw their jitter from ThreadSafeRandom; the overloads taking a
    /// System.Random are what the tests use to get a reproducible layout.
    /// </summary>
    public static class WorldEventGeometry
    {
        /// <summary>Jitter used by WorldEvent when it builds a theme's spawn anchors, in metres.</summary>
        public const float DefaultJitterMetres = 2.0f;

        /// <summary>
        /// Total angular width of one "edge" arc. Every position assigned to an arc is spread evenly
        /// across this width, centred on the arc's own angle.
        /// </summary>
        private const double EdgeArcWidthRadians = Math.PI / 6;   // 30 degrees

        private const double TwoPi = Math.PI * 2;

        /// <summary>
        /// <paramref name="points"/> positions evenly spaced around the circle of <paramref name="radius"/>
        /// about <paramref name="centre"/>, each facing the centre. The whole ring is rotated by a random
        /// offset so successive runs at the same anchor do not place their rifts on the same compass points;
        /// the spacing between adjacent points is exactly 2 pi / points before jitter either way.
        /// </summary>
        public static IReadOnlyList<Position> Ring(Position centre, float radius, int points, float jitter)
        {
            return Ring(centre, radius, points, jitter, NewRandom());
        }

        public static IReadOnlyList<Position> Ring(Position centre, float radius, int points, float jitter, Random rng)
        {
            var result = new List<Position>();

            if (centre == null || points < 1)
                return result;

            rng = rng ?? NewRandom();

            var rotation = rng.NextDouble() * TwoPi;
            var step = TwoPi / points;

            for (var i = 0; i < points; i++)
            {
                var angle = rotation + i * step;

                var dx = (float)(Math.Sin(angle) * radius);
                var dy = (float)(Math.Cos(angle) * radius);

                ApplyJitter(ref dx, ref dy, jitter, rng);

                result.Add(PlaceFacingCentre(centre, dx, dy));
            }

            return result;
        }

        /// <summary>
        /// The "walk in from the edges" shape: <paramref name="points"/> positions on the circle of
        /// <paramref name="radius"/>, but clustered onto 2 or 3 arcs rather than spread all the way round,
        /// so a wave arrives as two or three groups instead of surrounding the anchor. The arc count is
        /// drawn from the rng (and clamped to <paramref name="points"/>), the arcs are evenly spaced at a
        /// random rotation, and each arc's share of the positions is spread across
        /// <see cref="EdgeArcWidthRadians"/>. Exactly <paramref name="points"/> positions come back, all
        /// facing the centre.
        /// </summary>
        public static IReadOnlyList<Position> Edges(Position centre, float radius, int points, float jitter)
        {
            return Edges(centre, radius, points, jitter, NewRandom());
        }

        public static IReadOnlyList<Position> Edges(Position centre, float radius, int points, float jitter, Random rng)
        {
            var result = new List<Position>();

            if (centre == null || points < 1)
                return result;

            rng = rng ?? NewRandom();

            var arcs = Math.Min(points, 2 + rng.Next(0, 2));   // 2 or 3, never more than there are points
            var rotation = rng.NextDouble() * TwoPi;
            var arcStep = TwoPi / arcs;

            for (var i = 0; i < points; i++)
            {
                // round-robin assignment, so arc j holds ceil((points - j) / arcs) positions
                var arc = i % arcs;
                var indexInArc = i / arcs;
                var inThisArc = points / arcs + (arc < points % arcs ? 1 : 0);

                var spread = inThisArc <= 1
                    ? 0d
                    : (indexInArc / (double)(inThisArc - 1) - 0.5) * EdgeArcWidthRadians;

                var angle = rotation + arc * arcStep + spread;

                var dx = (float)(Math.Sin(angle) * radius);
                var dy = (float)(Math.Cos(angle) * radius);

                ApplyJitter(ref dx, ref dy, jitter, rng);

                result.Add(PlaceFacingCentre(centre, dx, dy));
            }

            return result;
        }

        /// <summary>
        /// <paramref name="points"/> positions sampled UNIFORMLY BY AREA inside the disc of
        /// <paramref name="radius"/> about <paramref name="centre"/> (WP-16, TECH-DESIGN 2.4/5.6), each
        /// facing the centre. Uniform-by-area sampling needs the radius drawn as
        /// <c>radius * sqrt(rng.NextDouble())</c> rather than a plain linear draw, which would bunch points
        /// near the centre - the sqrt is what spreads them evenly across the disc's area instead of its
        /// radius. There is no jitter parameter: the sampling itself is the spread, unlike Ring/Edges where
        /// jitter nudges an otherwise-fixed point.
        /// </summary>
        public static IReadOnlyList<Position> Disc(Position centre, float radius, int points)
        {
            return Disc(centre, radius, points, NewRandom());
        }

        public static IReadOnlyList<Position> Disc(Position centre, float radius, int points, Random rng)
        {
            var result = new List<Position>();

            if (centre == null || points < 1)
                return result;

            rng = rng ?? NewRandom();

            for (var i = 0; i < points; i++)
            {
                var r = radius * Math.Sqrt(rng.NextDouble());
                var bearing = rng.NextDouble() * TwoPi;

                var dx = (float)(Math.Sin(bearing) * r);
                var dy = (float)(Math.Cos(bearing) * r);

                result.Add(PlaceFacingCentre(centre, dx, dy));
            }

            return result;
        }

        /// <summary>One position: a copy of the centre, same coordinates, same facing, same instance.</summary>
        public static IReadOnlyList<Position> Single(Position centre)
        {
            var result = new List<Position>();

            if (centre != null)
                result.Add(new Position(centre));

            return result;
        }

        /// <summary>
        /// A fresh copy of <paramref name="basePosition"/> nudged by up to <paramref name="jitter"/> metres,
        /// keeping its facing. Used by the spawner when a wave has more creatures than anchors and reuses an
        /// anchor, and for the retry attempts after a failed EnterWorld (R16).
        /// </summary>
        public static Position Jitter(Position basePosition, float jitter, Random rng)
        {
            if (basePosition == null)
                return null;

            rng = rng ?? NewRandom();

            float dx = 0, dy = 0;
            ApplyJitter(ref dx, ref dy, jitter, rng);

            return Translate(basePosition, dx, dy);
        }

        /// <summary>
        /// Jitter as a single random offset of magnitude in [0, jitter] at a random bearing, NOT as
        /// independent X and Y nudges - that keeps the distance from the centre inside
        /// [radius - jitter, radius + jitter] instead of [radius - sqrt(2)*jitter, radius + sqrt(2)*jitter].
        /// </summary>
        private static void ApplyJitter(ref float dx, ref float dy, float jitter, Random rng)
        {
            if (jitter <= 0)
                return;

            var magnitude = rng.NextDouble() * jitter;
            var bearing = rng.NextDouble() * TwoPi;

            dx += (float)(Math.Sin(bearing) * magnitude);
            dy += (float)(Math.Cos(bearing) * magnitude);
        }

        /// <summary>
        /// A new position offset from <paramref name="origin"/> by (dx, dy) metres, facing back toward it.
        /// The facing is computed from the offset vector rather than from the two positions' coordinates,
        /// because SetPosition may have moved the result into an adjacent landblock, after which its X/Y
        /// are relative to a different block and the difference would be meaningless.
        /// </summary>
        private static Position PlaceFacingCentre(Position centre, float dx, float dy)
        {
            var placed = Translate(centre, dx, dy);

            if (dx != 0f || dy != 0f)
                placed.Rotate(new Vector3(-dx, -dy, 0));

            return placed;
        }

        private static Position Translate(Position origin, float dx, float dy)
        {
            var placed = new Position(origin.LandblockId.Raw,
                origin.PositionX, origin.PositionY, origin.PositionZ,
                origin.RotationX, origin.RotationY, origin.RotationZ, origin.RotationW,
                origin.Instance);

            // SetPosition recomputes the landblock and the land cell from the new X/Y, so an offset that
            // crosses a block boundary comes back addressed to the block it actually landed in. It is a
            // no-op for indoor cells, which carry their own cell id.
            placed.SetPosition(new Vector3(origin.PositionX + dx, origin.PositionY + dy, origin.PositionZ));

            return placed;
        }

        /// <summary>
        /// int.MaxValue is excluded because ThreadSafeRandom.Next(int, int) is INCLUSIVE of its upper bound
        /// and computes max + 1 internally, which overflows at int.MaxValue.
        /// </summary>
        private static Random NewRandom()
        {
            return new Random(ThreadSafeRandom.Next(0, int.MaxValue - 1));
        }
    }
}
