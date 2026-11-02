using System.Text.Json.Serialization;

namespace ACE.Server.WorldEvents.Defs
{
    /// <summary>
    /// goals.json "townReach": a run of this goal must stand a short walk from where a Town Network portal drops players.
    /// A start whose spot is farther than <see cref="MaxWalk"/> from every town's drop is re-rolled to the edge of the nearest
    /// towns, and logged. Absent: no check (staff can still start anywhere). Validated at load by
    /// <see cref="WorldEventTownReach.Validate"/>; a goal with a bad block is dropped.
    ///
    /// Ported from DreamWeave #17's WorldEventTownReach.cs. The contributor's maxWalk read a world_events_hold_town_reach
    /// setting; the fork's Hold settings registry has no such row, so the goals.json value is the only source here.
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
