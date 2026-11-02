using System;
using System.Collections.Generic;

using ACE.Entity;

namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// The nominal circular arena a run's presence is measured against (and, later, hazards): a centre, a
    /// horizontal radius and a vertical half-height. Pure geometry - no landblock, physics or terrain - so
    /// the whole shape is unit testable without a world.
    ///
    /// <see cref="Contains"/> deliberately mirrors WorldEventAudienceSampler.Sample: the SAME landblock
    /// instance is required first (a realm copy of a landblock is a different place with the same
    /// geometry), then the horizontal distance, which Position.Distance2D computes correctly across
    /// landblock boundaries outdoors. The vertical limit keeps a player on a cliff above, or in a dungeon
    /// below, from counting as present.
    /// </summary>
    public sealed class WorldEventArena
    {
        public Position Centre { get; }
        public float Radius { get; }
        public float HalfHeight { get; }

        public WorldEventArena(Position centre, float radius, float halfHeight)
        {
            Centre = centre ?? throw new ArgumentNullException(nameof(centre));
            Radius = radius;
            HalfHeight = halfHeight;
        }

        /// <summary>
        /// True when <paramref name="p"/> is in the same landblock instance as the centre, within
        /// <see cref="Radius"/> horizontally (inclusive) and within <see cref="HalfHeight"/> vertically
        /// (inclusive). A null position is never inside.
        /// </summary>
        public bool Contains(Position p)
        {
            if (p == null || p.Instance != Centre.Instance)
                return false;

            if (Centre.Distance2D(p) > Radius)
                return false;

            return Math.Abs(CentreZDelta(p)) <= HalfHeight;
        }

        private float CentreZDelta(Position p) => p.PositionZ - Centre.PositionZ;

        /// <summary>
        /// The ring marker count for this circle: clamp(ceil(2 pi R / spacing), min, max). The same formula
        /// as BattlegroundZoneMarkers.Count, which is not reused because it pins its own 8..32 clamp and
        /// its own spacing clamp. A non-positive spacing yields <paramref name="min"/>.
        /// </summary>
        public int RingPointCount(double spacing, int min, int max)
        {
            if (max < min)
                max = min;

            if (spacing <= 0 || double.IsNaN(spacing))
                return min;

            var raw = Math.Ceiling(2.0 * Math.PI * Radius / spacing);

            if (double.IsNaN(raw) || raw <= min)
                return min;

            return raw >= max ? max : (int)raw;
        }

        /// <summary>
        /// <paramref name="count"/> evenly spaced positions on the rim, at the centre's Z, each facing the
        /// centre, in the centre's instance. Delegates to <see cref="WorldEventGeometry.Ring"/> with no
        /// jitter, so cross-landblock offsets arrive correctly addressed. The ring is rotated by a random
        /// offset drawn from <paramref name="rng"/> (pass a seeded Random for a reproducible layout).
        /// Z is the centre's; the spawner replaces it with a terrain probe.
        /// </summary>
        public IReadOnlyList<Position> RingPoints(int count, Random rng = null)
        {
            return WorldEventGeometry.Ring(Centre, Radius, count, 0f, rng);
        }

        /// <summary><see cref="RingPoints(int, Random)"/> with the count taken from <see cref="RingPointCount"/>.</summary>
        public IReadOnlyList<Position> RingPoints(double spacing, int min, int max, Random rng = null)
        {
            return RingPoints(RingPointCount(spacing, min, max), rng);
        }

        /// <summary>
        /// True when the circle extends past the edge of the centre's 192 m landblock, so ring markers or
        /// a probe would land in a neighbouring block. An indoor centre never crosses (it has no
        /// neighbouring outdoor block to spill into).
        /// </summary>
        public bool CrossesBlockEdge()
        {
            if (Centre.Indoors)
                return false;

            const float len = Position.BlockLength;

            return Centre.PositionX - Radius < 0f || Centre.PositionX + Radius > len ||
                   Centre.PositionY - Radius < 0f || Centre.PositionY + Radius > len;
        }

        /// <summary>
        /// One position uniformly distributed BY AREA inside the disc, at the centre's Z. Delegates to
        /// <see cref="WorldEventGeometry.Disc(Position, float, int, Random)"/>.
        /// </summary>
        public Position RandomPointInside(Random rng)
        {
            var pts = WorldEventGeometry.Disc(Centre, Radius, 1, rng);

            return pts.Count > 0 ? pts[0] : null;
        }
    }
}