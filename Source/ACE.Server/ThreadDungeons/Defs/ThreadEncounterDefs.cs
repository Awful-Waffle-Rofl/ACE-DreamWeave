using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ACE.Server.ThreadDungeons.Defs
{
    /// <summary>
    /// Root of Content/dungeons/dynamic/encounters.json (Docs/Threads/ENCOUNTERS.md). Optional, like
    /// puzzle-gates.json: a missing, malformed or unsupported-version file means "no encounter types", never a
    /// refused store. Types and per-dungeon rows are kept as raw <see cref="JsonElement"/>s so one wrongly typed
    /// entry costs only that entry (the puzzle-gates.json rule, PuzzleSiteDefs.cs).
    /// </summary>
    public class ThreadEncounterFileDef
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("types")]
        public List<JsonElement> Types { get; set; } = new List<JsonElement>();

        /// <summary>index.json dungeon id -> { "disabled": true, "reason": "..." }.</summary>
        [JsonPropertyName("dungeons")]
        public Dictionary<string, JsonElement> Dungeons { get; set; } = new Dictionary<string, JsonElement>();

        /// <summary>The shared encounter prize table, { "entries": [ { id, weight, wcids+qty | salvage } ] } (ThreadEncounterPrizeTable.Parse). Undefined when absent.</summary>
        [JsonPropertyName("prize")]
        public JsonElement Prize { get; set; }

        /// <summary>
        /// Optional: the weight of each encounter count, index = the number of encounters a run tries to place (owner ruling
        /// 2026-11-11). Validated by ThreadEncounterStore.ParseCountWeights. Undefined when absent: the per-slot
        /// dynamic_dungeons_encounters_slot_chance roll is used instead.
        /// </summary>
        [JsonPropertyName("countWeights")]
        public JsonElement CountWeights { get; set; }
    }

    /// <summary>
    /// One encounter type. <see cref="Kind"/> names the registered implementation
    /// (<see cref="ACE.Server.ThreadDungeons.ThreadEncounterTypes"/>); <see cref="Id"/> names this row, so one kind
    /// may appear as several rows with different placement rules or config.
    /// </summary>
    public class ThreadEncounterTypeDef
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("kind")]
        public string Kind { get; set; }

        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = true;

        /// <summary>Relative draw weight among the types that fit a slot. 0 = never drawn at random (admin place only).</summary>
        [JsonPropertyName("weight")]
        public double Weight { get; set; } = 1.0;

        /// <summary>
        /// The most encounters of this row's KIND one run may get from the picker (ThreadEncounterPicker.KindCapReached).
        /// Absent (null) = no cap, so a row without the key draws exactly as before. At least 1 when present.
        /// </summary>
        [JsonPropertyName("maxPerRun")]
        public int? MaxPerRun { get; set; }

        [JsonPropertyName("placement")]
        public ThreadEncounterPlacementDef Placement { get; set; } = new ThreadEncounterPlacementDef();

        /// <summary>
        /// The type-specific block, opaque to the framework and read only by the implementation for
        /// <see cref="Kind"/>. A detached clone (the parser calls Clone), so it outlives the parsed document.
        /// ValueKind Undefined when the row has no config.
        /// </summary>
        [JsonPropertyName("config")]
        public JsonElement Config { get; set; }
    }

    /// <summary>
    /// The placement requirements every type declares. All distances are metres; 0 (the default) means no
    /// constraint. Depth applies to dungeon runs only (outdoor points carry none); clearance is the point's
    /// curated clearance in a dungeon and a runtime ground probe outdoors (ThreadEncounterPass).
    /// </summary>
    public class ThreadEncounterPlacementDef
    {
        [JsonPropertyName("minClearance")]
        public float MinClearance { get; set; }

        [JsonPropertyName("minDepth")]
        public int? MinDepth { get; set; }

        [JsonPropertyName("maxDepth")]
        public int? MaxDepth { get; set; }

        /// <summary>Distance from the run's entry: walked through the dungeon walk graph when one exists, else straight-line.</summary>
        [JsonPropertyName("minEntryWalk")]
        public float MinEntryWalk { get; set; }

        [JsonPropertyName("minBossDist")]
        public float MinBossDist { get; set; }

        /// <summary>Distance from every other encounter of the run AND from this run's puzzle sites.</summary>
        [JsonPropertyName("minSpacing")]
        public float MinSpacing { get; set; }

        /// <summary>"dungeon" and/or "outdoor". Empty or absent = both.</summary>
        [JsonPropertyName("runKinds")]
        public List<string> RunKinds { get; set; } = new List<string>();

        /// <summary>Does this type allow a run of the given kind?</summary>
        public bool AllowsRunKind(bool isOutdoor)
        {
            if (RunKinds == null || RunKinds.Count == 0)
                return true;

            return RunKinds.Contains(isOutdoor ? DungeonEntryDef.KindOutdoor : DungeonEntryDef.KindDungeon);
        }
    }

    /// <summary>A per-dungeon row: today only the kill switch.</summary>
    public class ThreadEncounterDungeonDef
    {
        [JsonPropertyName("disabled")]
        public bool Disabled { get; set; }

        [JsonPropertyName("reason")]
        public string Reason { get; set; }
    }
}
