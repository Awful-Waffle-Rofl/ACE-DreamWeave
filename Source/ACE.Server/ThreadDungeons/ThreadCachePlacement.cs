using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.ThreadDungeons.Defs;

namespace ACE.Server.ThreadDungeons
{
    public enum CacheCandidateStage
    {
        InFront = 1,
        Ring = 2,
        Curated = 3,
        OwnerSpot = 4,
    }

    /// <summary>One place a Thread Cache may be tried. Landblock-relative coordinates, owner's rotation.</summary>
    public readonly struct CacheCandidate
    {
        public CacheCandidate(CacheCandidateStage stage, uint cell, float x, float y, float z, float qz, float qw, bool recomputeCell)
        {
            Stage = stage;
            Cell = cell;
            X = x;
            Y = y;
            Z = z;
            QZ = qz;
            QW = qw;
            RecomputeCell = recomputeCell;
        }

        public CacheCandidateStage Stage { get; }
        public uint Cell { get; }
        public float X { get; }
        public float Y { get; }
        public float Z { get; }
        public float QZ { get; }
        public float QW { get; }

        /// <summary>True for a point computed from the owner's position, whose cell must be re-resolved (Pet.cs:259).</summary>
        public bool RecomputeCell { get; }
    }

    /// <summary>
    /// The Thread Cache placement chain's candidate order (Docs/Threads/POOLED-LOOT-CACHE-DESIGN.md section 5).
    /// PURE, like DungeonPlacementFallback.OrderCandidates: no world, no physics, no PropertyManager. The
    /// executor (ThreadCachePlacer) resolves cells, checks line of sight, enters the world and re-checks
    /// height and separation.
    /// </summary>
    public static class ThreadCachePlacement
    {
        public const float InFrontDistance = 2.0f;
        public const int RingHeadings = 8;
        public const float CuratedRadius = 15.0f;
        public const float MinSeparation = 1.5f;
        public const float MaxZDelta = 2.0f;

        /// <summary>Position.InFrontOf's own bump height (ACE.Entity/Position.cs:117-138).</summary>
        public const float BumpHeight = 0.05f;

        public static readonly IReadOnlyList<float> RingRadii = new[] { 1.5f, 3.0f };

        /// <summary>
        /// Stage 1: 2 m in front. Stage 2: 8 headings at 1.5 m and 3 m, nearest to the facing first (equal
        /// offsets: positive first; within a heading, 1.5 m first). Stage 3: curated points on the owner's
        /// landblock within 15 m (inclusive), nearest first, ties on (cell, x, y, z). Stage 4: the owner's exact
        /// spot. Any candidate closer than 1.5 m to an exclusion (a placed cache, the summoned exit) is dropped.
        /// </summary>
        public static List<CacheCandidate> BuildCandidates(uint ownerCell, float ownerX, float ownerY, float ownerZ, float ownerQZ, float ownerQW,
            IReadOnlyList<DungeonSpawnPointDef> curated, IReadOnlyList<(float X, float Y, float Z)> exclusions)
        {
            var result = new List<CacheCandidate>();

            // The heading formula Position.InFrontOf uses.
            var heading = Math.Atan2(2 * ownerQW * ownerQZ, 1 - 2 * ownerQZ * ownerQZ);

            TryAdd(result, Offset(CacheCandidateStage.InFront, ownerCell, ownerX, ownerY, ownerZ, ownerQZ, ownerQW, heading, InFrontDistance), exclusions);

            var step = 2 * Math.PI / RingHeadings;

            foreach (var k in RingStepOrder())
            {
                foreach (var radius in RingRadii)
                    TryAdd(result, Offset(CacheCandidateStage.Ring, ownerCell, ownerX, ownerY, ownerZ, ownerQZ, ownerQW, heading + k * step, radius), exclusions);
            }

            if (curated != null)
            {
                var landblock = ownerCell >> 16;
                var maxSq = CuratedRadius * CuratedRadius;

                var near = curated
                    .Where(p => p != null && p.Curated && (p.Cell >> 16) == landblock)
                    .Select(p => (Point: p, DistSq: DistanceSquared(p.X, p.Y, p.Z, ownerX, ownerY, ownerZ)))
                    .Where(t => t.DistSq <= maxSq)
                    .OrderBy(t => t.DistSq).ThenBy(t => t.Point.Cell).ThenBy(t => t.Point.X).ThenBy(t => t.Point.Y).ThenBy(t => t.Point.Z);

                foreach (var t in near)
                    TryAdd(result, new CacheCandidate(CacheCandidateStage.Curated, t.Point.Cell, t.Point.X, t.Point.Y, t.Point.Z, t.Point.QZ, t.Point.QW, recomputeCell: false), exclusions);
            }

            TryAdd(result, new CacheCandidate(CacheCandidateStage.OwnerSpot, ownerCell, ownerX, ownerY, ownerZ, ownerQZ, ownerQW, recomputeCell: false), exclusions);

            return result;
        }

        /// <summary>0, +1, -1, +2, -2, +3, -3, +4: heading steps by angular distance from the facing.</summary>
        internal static IEnumerable<int> RingStepOrder()
        {
            yield return 0;

            for (var k = 1; k < RingHeadings / 2; k++)
            {
                yield return k;
                yield return -k;
            }

            yield return RingHeadings / 2;
        }

        /// <summary>True when (x, y, z) is strictly closer than <see cref="MinSeparation"/> to any exclusion.</summary>
        public static bool IsExcluded(float x, float y, float z, IReadOnlyList<(float X, float Y, float Z)> exclusions)
        {
            if (exclusions == null)
                return false;

            var minSq = (double)MinSeparation * MinSeparation;

            foreach (var e in exclusions)
                if (DistanceSquared(x, y, z, e.X, e.Y, e.Z) < minSq)
                    return true;

            return false;
        }

        private static CacheCandidate Offset(CacheCandidateStage stage, uint cell, float x, float y, float z, float qz, float qw, double angle, float distance)
        {
            var dx = -Math.Sin(angle) * distance;
            var dy = Math.Cos(angle) * distance;

            return new CacheCandidate(stage, cell, (float)(x + dx), (float)(y + dy), z + BumpHeight, qz, qw, recomputeCell: true);
        }

        private static void TryAdd(List<CacheCandidate> result, CacheCandidate candidate, IReadOnlyList<(float X, float Y, float Z)> exclusions)
        {
            if (!IsExcluded(candidate.X, candidate.Y, candidate.Z, exclusions))
                result.Add(candidate);
        }

        private static double DistanceSquared(float ax, float ay, float az, float bx, float by, float bz)
        {
            var dx = (double)ax - bx;
            var dy = (double)ay - by;
            var dz = (double)az - bz;

            return dx * dx + dy * dy + dz * dz;
        }
    }
}
