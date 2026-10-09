using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.ThreadDungeons.Defs;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// Where a run creature refused at its own spawn point is tried next (dynamic_dungeons_placement_fallback_attempts).
    ///
    /// The refusal this exists for is Landblock.AddWorldObjectInternal's AddPhysicsObj failure
    /// (Landblock.cs:1595-1617): the physics layer cannot fit the creature's body at that exact spot. Seen in
    /// prod on 2026-09-13 for wcid 72180 (the boss) and two 46780 elites in one marauders_lair run. The
    /// dungeon fit filter measures height only, so a wide creature can pass it and still be refused here, and
    /// a refused boss permanently withholds the boss share of the run's clear progress.
    ///
    /// PURE: ordering only. The spawner creates a FRESH object per attempt rather than re-positioning the
    /// refused one - after a failed AddPhysicsObj the old object holds an initialized PhysicsObj with no cell
    /// and a cleared CurrentLandblock, and the spawner destroys it exactly as it always did.
    /// </summary>
    public static class DungeonPlacementFallback
    {
        /// <summary>
        /// The candidate points, best first, capped at <paramref name="maxAttempts"/>.
        ///
        ///   - BOSS: every other curated point on the same landblock, nearest to the boss anchor first - the
        ///     boss should stay where the run's headline fight was designed to be.
        ///   - TRASH / ELITE: curated points no creature is standing on (<paramref name="isUsed"/> false) first,
        ///     then used ones; nearest to the refused point first within each group. An unused point cannot
        ///     collide with a creature already placed there.
        ///
        /// Always excluded: the refused point itself, and the boss anchor for a non-boss (a trash creature on
        /// the anchor would stand inside the boss). Duplicate locations collapse to one, and ties break on
        /// (cell, x, y, z) so a given plan always yields the same order.
        ///
        /// Distance is straight-line over the points' landblock-relative X/Y/Z. Every point is on the run's
        /// landblock (ThreadDungeonStore rejects a file where any point is not), and the filter below enforces
        /// it again because a distance across two landblocks would be meaningless.
        /// </summary>
        /// <param name="isUsed">
        /// For TRASH/ELITE only (ignored for a boss retry): pushes an already-claimed point to the BACK of the
        /// candidate order rather than excluding it. A curated point standing at the overflow-sharing round's
        /// max load is still a valid retry target, just a worse one than an empty point - so an overflow plan
        /// where every curated point is already claimed still returns a full candidate list instead of an
        /// empty one.
        /// </param>
        public static List<DungeonSpawnPointDef> OrderCandidates(IReadOnlyList<DungeonSpawnPointDef> curated, DungeonSpawnPointDef refused,
            DungeonSpawnPointDef bossAnchor, bool isBoss, Func<DungeonSpawnPointDef, bool> isUsed, int maxAttempts)
        {
            var result = new List<DungeonSpawnPointDef>();

            if (curated == null || refused == null || maxAttempts <= 0)
                return result;

            var landblock = refused.Cell >> 16;
            var origin = isBoss && bossAnchor != null ? bossAnchor : refused;
            var seen = new HashSet<(uint, float, float, float)>();
            var candidates = new List<DungeonSpawnPointDef>();

            foreach (var p in curated)
            {
                if (p == null || !p.Curated || (p.Cell >> 16) != landblock)
                    continue;

                if (SameLocation(p, refused) || (bossAnchor != null && SameLocation(p, bossAnchor)))
                    continue;

                if (seen.Add(KeyOf(p)))
                    candidates.Add(p);
            }

            IOrderedEnumerable<DungeonSpawnPointDef> ordered = isBoss
                ? candidates.OrderBy(p => DistanceSquared(p, origin))
                : candidates.OrderBy(p => isUsed != null && isUsed(p) ? 1 : 0).ThenBy(p => DistanceSquared(p, origin));

            return ordered
                .ThenBy(p => p.Cell).ThenBy(p => p.X).ThenBy(p => p.Y).ThenBy(p => p.Z)
                .Take(maxAttempts)
                .ToList();
        }

        /// <summary>A point's identity for "is somebody standing here": cell plus landblock-relative position.</summary>
        public static (uint, float, float, float) KeyOf(DungeonSpawnPointDef p) => (p.Cell, (float)p.X, (float)p.Y, (float)p.Z);

        internal static bool SameLocation(DungeonSpawnPointDef a, DungeonSpawnPointDef b)
            => a != null && b != null && KeyOf(a) == KeyOf(b);

        internal static double DistanceSquared(DungeonSpawnPointDef a, DungeonSpawnPointDef b)
        {
            var dx = (double)a.X - b.X;
            var dy = (double)a.Y - b.Y;
            var dz = (double)a.Z - b.Z;

            return dx * dx + dy * dy + dz * dz;
        }
    }
}
