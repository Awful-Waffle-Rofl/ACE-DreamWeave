using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity;
using ACE.Server.WorldEvents.Defs;

namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// The Town Network reach rule (goals.json townReach): an event location is reachable when a Town Network portal drops
    /// players within townReach.maxWalk of it. The town list is <see cref="WorldEventTownIndex"/> (the portals of landblock
    /// 0x0007, so only towns with a portal destination count). Every rule here is pure (D6); the Hold start-spot rule applies
    /// them and re-rolls an unreachable start. Ported from DreamWeave #17 without the chain inheritance (the chain is not
    /// ported).
    /// </summary>
    public static class WorldEventTownReach
    {
        /// <summary>The nearest town to <paramref name="position"/> and how far its drop is; null with no towns or indoors.</summary>
        public static (WorldEventTown Town, float Distance)? NearestDrop(Position position, IReadOnlyList<WorldEventTown> towns) =>
            WorldEventTownIndex.Nearest(position, towns);

        /// <summary>
        /// True when a Town Network drop lies within <paramref name="maxWalk"/> of <paramref name="position"/>. Indoors, or with
        /// no towns loaded, nothing is reachable.
        /// </summary>
        public static bool IsReachable(Position position, IReadOnlyList<WorldEventTown> towns, double maxWalk)
        {
            var nearest = NearestDrop(position, towns);

            return nearest != null && nearest.Value.Distance <= maxWalk;
        }

        /// <summary>The global-metre form of <see cref="IsReachable"/>, for a spot that is not yet a Position.</summary>
        public static bool IsReachable(double globalX, double globalY, IReadOnlyList<WorldEventTown> towns, double maxWalk)
        {
            if (towns == null || towns.Count == 0)
                return false;

            var best = towns.Min(t => (t.GlobalX - globalX) * (t.GlobalX - globalX) + (t.GlobalY - globalY) * (t.GlobalY - globalY));

            return Math.Sqrt(best) <= maxWalk;
        }

        /// <summary>
        /// The towns a re-roll tries: every named town by distance from the asked spot (global metres), nearest first, minus
        /// <paramref name="exclude"/>, at most <see cref="TownReachDef.Towns"/> (kept within 1 to 10).
        /// </summary>
        public static List<WorldEventTown> RerollTowns(IReadOnlyList<WorldEventTown> towns, double originX, double originY,
            TownReachDef reach, ICollection<string> exclude = null)
        {
            reach ??= new TownReachDef();

            return (towns ?? Array.Empty<WorldEventTown>())
                .Where(t => !string.IsNullOrWhiteSpace(t.Name) && (exclude == null || !exclude.Contains(t.Name)))
                .OrderBy(t => (t.GlobalX - originX) * (t.GlobalX - originX) + (t.GlobalY - originY) * (t.GlobalY - originY))
                .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .Take(Math.Clamp(reach.Towns, 1, 10))
                .ToList();
        }

        /// <summary>
        /// The candidate spots a re-roll near <paramref name="town"/> tries, in global metres: rings from
        /// <see cref="TownReachDef.SpotMinDistance"/> to <see cref="TownReachDef.SpotMaxDistance"/> (20 m apart), each at
        /// <paramref name="bearings"/> evenly spread bearings starting from a random offset, so two re-rolls near one town do not
        /// always land on the same spot. Pure over <paramref name="rng"/>.
        /// </summary>
        public static List<(double X, double Y)> SpotsNear(WorldEventTown town, TownReachDef reach, Random rng, int bearings = 12)
        {
            reach ??= new TownReachDef();
            rng ??= new Random();
            bearings = Math.Max(1, bearings);

            var spots = new List<(double, double)>();
            var min = Math.Max(0, reach.SpotMinDistance);
            var max = Math.Max(min, reach.SpotMaxDistance);
            var start = rng.NextDouble() * 360.0;

            for (var r = min; r <= max + 1e-9; r += 20)
            {
                for (var i = 0; i < bearings; i++)
                {
                    var degrees = start + 360.0 * i / bearings;
                    var radians = degrees * Math.PI / 180.0;

                    spots.Add((town.GlobalX + r * Math.Sin(radians), town.GlobalY + r * Math.Cos(radians)));
                }
            }

            return spots;
        }

        /// <summary>The log text for a spot: "Holtburg's portal drop 1234 m away", or why there is none.</summary>
        public static string Describe(Position position, IReadOnlyList<WorldEventTown> towns)
        {
            var nearest = NearestDrop(position, towns);

            if (nearest == null)
                return position != null && position.Indoors ? "indoors, no town drop counts" : "no town loaded";

            return $"{nearest.Value.Town.Name}'s portal drop {nearest.Value.Distance:0} m away";
        }

        /// <summary>The goals.json check: null when <paramref name="reach"/> is absent or valid, else the reason (the goal is dropped).</summary>
        public static string Validate(TownReachDef reach)
        {
            if (reach == null)
                return null;

            if (!double.IsFinite(reach.MaxWalk) || reach.MaxWalk < 50 || reach.MaxWalk > 1000)
                return "has an invalid townReach.maxWalk (50 to 1000)";

            if (!double.IsFinite(reach.SpotMinDistance) || !double.IsFinite(reach.SpotMaxDistance) || reach.SpotMinDistance < 20
                || reach.SpotMinDistance > 400 || reach.SpotMaxDistance <= reach.SpotMinDistance || reach.SpotMaxDistance > reach.MaxWalk)
                return "has an invalid townReach spot distance (spotMinDistance 20 to 400, spotMaxDistance above it and up to maxWalk)";

            if (reach.Towns < 1 || reach.Towns > 10)
                return "has an invalid townReach.towns (1 to 10)";

            return null;
        }
    }
}
