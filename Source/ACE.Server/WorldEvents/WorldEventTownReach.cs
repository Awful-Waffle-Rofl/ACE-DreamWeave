using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

using ACE.Entity;
using ACE.Server.Entity;
using ACE.Server.WorldEvents.Defs;

namespace ACE.Server.WorldEvents.Defs
{
    /// <summary>
    /// goals.json "townReach": every location a run of this goal uses (the first anchor, and every chained stage after
    /// it) must be a short walk from where a Town Network portal drops players. A start whose spot is farther than
    /// <see cref="MaxWalk"/> from every town's drop is re-rolled to the edge of the nearest towns, and logged. Absent:
    /// no check (staff can still start anywhere).
    /// </summary>
    public class TownReachDef
    {
        /// <summary>The farthest the event may stand from a town's portal drop, in metres (50 to 1000). Default 250.</summary>
        [JsonPropertyName("maxWalk")]
        public double MaxWalk { get; set; } = 250;

        /// <summary>A re-rolled spot stands at least this far from the drop, in metres (20 to 400). Default 60.</summary>
        [JsonPropertyName("spotMinDistance")]
        public double SpotMinDistance { get; set; } = 60;

        /// <summary>And at most this far (above spotMinDistance, up to maxWalk). Default 160.</summary>
        [JsonPropertyName("spotMaxDistance")]
        public double SpotMaxDistance { get; set; } = 160;

        /// <summary>How many towns a re-roll tries, nearest first (1 to 10). Default 4.</summary>
        [JsonPropertyName("towns")]
        public int Towns { get; set; } = 4;
    }
}

namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// The Town Network reach rule (2026-10-19): an event location is reachable when a Town Network portal drops
    /// players within townReach.maxWalk of it. The town list is WorldEventTownIndex (the portals of landblock
    /// 0x0007, so only towns with a portal destination count). The rules are pure; WorldEventManager.TryStart applies
    /// them and re-rolls an unreachable start.
    /// </summary>
    public static class WorldEventTownReach
    {
        /// <summary>The nearest town to <paramref name="position"/> and how far its drop is (D6 - pure); null with no towns or indoors.</summary>
        public static (WorldEventTown Town, float Distance)? NearestDrop(Position position, IReadOnlyList<WorldEventTown> towns)
        {
            return WorldEventTownIndex.Nearest(position, towns);
        }

        /// <summary>
        /// True when a Town Network drop lies within <paramref name="maxWalk"/> of <paramref name="position"/> (D6 - pure).
        /// Indoors, or with no towns loaded, nothing is reachable.
        /// </summary>
        public static bool IsReachable(Position position, IReadOnlyList<WorldEventTown> towns, double maxWalk)
        {
            var nearest = NearestDrop(position, towns);

            return nearest != null && nearest.Value.Distance <= maxWalk;
        }

        /// <summary>
        /// The towns a re-roll tries (D6 - pure): every town by distance from the asked spot (global metres), nearest
        /// first, minus <paramref name="exclude"/>, at most <see cref="TownReachDef.Towns"/>.
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

        /// <summary>The log text for a spot (D6 - pure): "Holtburg's drop 1234 m away" or "no town loaded".</summary>
        public static string Describe(Position position, IReadOnlyList<WorldEventTown> towns)
        {
            var nearest = NearestDrop(position, towns);

            if (nearest == null)
                return position != null && position.Indoors ? "indoors, no town drop counts" : "no town loaded";

            return $"{nearest.Value.Town.Name}'s portal drop {nearest.Value.Distance:0} m away";
        }

        /// <summary>The goals.json check (D6 - pure). Null when valid.</summary>
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

        /// <summary>
        /// The reach rule a run follows (D6 - pure): its goal's own, else the one a chain carried over from its first stage,
        /// else none.
        /// </summary>
        public static TownReachDef Effective(TownReachDef goalReach, TownReachDef inherited) => goalReach ?? inherited;
    }
}
