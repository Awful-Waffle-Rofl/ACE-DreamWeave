using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

using ACE.Server.PuzzleGates;

namespace ACE.Server.ThreadDungeons.Defs
{
    /// <summary>
    /// Root of Content/dungeons/dynamic/puzzle-gates.json, written by the puzzle-site tool. Optional, like
    /// clearance.json: a missing, malformed or unsupported-version file means "no puzzle sites", never a
    /// refused store. See <see cref="ThreadDungeonStore.GetPuzzleSites"/>.
    ///
    /// The per-dungeon values are kept as raw <see cref="JsonElement"/>s on purpose: one site with a wrongly
    /// typed field must cost only that site, and a typed deserialize would throw away the whole file for it.
    /// </summary>
    public class PuzzleGatesFileDef
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        /// <summary>index.json dungeon id -> { "sites": [ ... ] }.</summary>
        [JsonPropertyName("dungeons")]
        public Dictionary<string, JsonElement> Dungeons { get; set; } = new Dictionary<string, JsonElement>();
    }

    /// <summary>What a puzzle site is for.</summary>
    public enum PuzzleSiteKind
    {
        /// <summary>A doorway the puzzle holds shut on the way in.</summary>
        Gate,

        /// <summary>A doorway guarding the boss reward; only valid in a dungeon with a bossAnchor.</summary>
        Reward,
    }

    /// <summary>What stands in the doorway.</summary>
    public enum PuzzleGateModelKind
    {
        Door,
        Barrier,
        Resident,
    }

    /// <summary>A placed point. Cells are hex strings in the file ("0x0150018A"), parsed into <see cref="Cell"/>.</summary>
    public class PuzzleSitePointDef
    {
        [JsonPropertyName("cell")]
        public string CellHex { get; set; }

        [JsonIgnore]
        public uint Cell { get; set; }

        [JsonPropertyName("x")] public float X { get; set; }
        [JsonPropertyName("y")] public float Y { get; set; }
        [JsonPropertyName("z")] public float Z { get; set; }
    }

    /// <summary>Measured doorway aperture, metres.</summary>
    public class PuzzleDoorwayDef
    {
        [JsonPropertyName("width")] public float Width { get; set; }
        [JsonPropertyName("height")] public float Height { get; set; }
        [JsonPropertyName("ceiling")] public float Ceiling { get; set; }
    }

    /// <summary>The thing that closes the doorway.</summary>
    public class PuzzleGateModelDef
    {
        /// <summary>"door" | "barrier" | "resident".</summary>
        [JsonPropertyName("kind")]
        public string KindName { get; set; }

        [JsonIgnore]
        public PuzzleGateModelKind Kind { get; set; }

        /// <summary>Weenie class id. Required for door and barrier; ignored for resident.</summary>
        [JsonPropertyName("wcid")]
        public uint Wcid { get; set; }

        [JsonPropertyName("scale")]
        public float Scale { get; set; } = 1f;

        /// <summary>Barrier panel count (>= 1 for a barrier).</summary>
        [JsonPropertyName("panels")]
        public int Panels { get; set; } = 1;

        /// <summary>Resident gate: the existing placed object's guid, hex string. Null otherwise.</summary>
        [JsonPropertyName("residentGuid")]
        public string ResidentGuidHex { get; set; }

        [JsonIgnore]
        public uint ResidentGuid { get; set; }
    }

    /// <summary>
    /// Optional per-site layout numbers as written in the file; every field is optional and an absent one keeps the
    /// admin default (PuzzleLayoutParams.Default). Ranges are PuzzleGateTunables.Min/Max*.
    /// </summary>
    public class PuzzleLayoutDef
    {
        [JsonPropertyName("gateDistance")] public float? GateDistance { get; set; }
        [JsonPropertyName("lightHeight")] public float? LightHeight { get; set; }
        [JsonPropertyName("indicatorHeight")] public float? IndicatorHeight { get; set; }
        [JsonPropertyName("hubHeight")] public float? HubHeight { get; set; }
    }

    /// <summary>One puzzle site in one dungeon.</summary>
    public class PuzzleSiteDef
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }

        /// <summary>"gate" | "reward".</summary>
        [JsonPropertyName("kind")]
        public string KindName { get; set; }

        [JsonIgnore]
        public PuzzleSiteKind Kind { get; set; }

        /// <summary>Approach-side standing point.</summary>
        [JsonPropertyName("anchor")]
        public PuzzleSitePointDef Anchor { get; set; }

        /// <summary>Degrees about +Z; forward = (-sin yaw, cos yaw).</summary>
        [JsonPropertyName("yaw")]
        public float Yaw { get; set; }

        [JsonPropertyName("doorway")]
        public PuzzleDoorwayDef Doorway { get; set; }

        /// <summary>Largest lever count the doorway fits, 1-5.</summary>
        [JsonPropertyName("maxN")]
        public int MaxN { get; set; }

        /// <summary>The puzzle types this site allows, as written in the file.</summary>
        [JsonPropertyName("types")]
        public List<string> TypeNames { get; set; } = new List<string>();

        /// <summary><see cref="TypeNames"/> parsed, de-duplicated, in file order.</summary>
        [JsonIgnore]
        public List<PuzzleGateType> Types { get; set; } = new List<PuzzleGateType>();

        [JsonPropertyName("gateModel")]
        public PuzzleGateModelDef GateModel { get; set; }

        /// <summary>Spots the shuffle lever hops between; at least 2 when shuffle is listed.</summary>
        [JsonPropertyName("shuffleSpots")]
        public List<PuzzleSitePointDef> ShuffleSpots { get; set; } = new List<PuzzleSitePointDef>();

        [JsonPropertyName("toolVersion")]
        public string ToolVersion { get; set; }

        /// <summary>The optional "layout" object as written; null when absent.</summary>
        [JsonPropertyName("layout")]
        public PuzzleLayoutDef LayoutDef { get; set; }

        /// <summary>The validated layout: the file's numbers over the admin defaults. Filled in by validation.</summary>
        [JsonIgnore]
        public PuzzleLayoutParams Layout { get; set; } = PuzzleLayoutParams.Default;

        // ---- the per-site kill switch (optional; absent = in use) ----

        /// <summary>
        /// The site's kill switch: true keeps it out of every run (never a gate, never the reward scene). The store
        /// still loads it, so /puzzlegate site can list and place it for inspection. /dd reload re-reads the file, so
        /// a site can be pulled without a deploy; puzzles a run already placed keep theirs.
        /// </summary>
        [JsonPropertyName("disabled")]
        public bool Disabled { get; set; }

        /// <summary>
        /// Role-specific switch: true keeps the site from hosting the REWARD scene (a reward site, or a gate site
        /// standing in for one at arming) while it may still be picked as a gate. Set by puzzlegate-sweep when only
        /// the reward-host role failed, since a reward-host failure never blocks a doorway.
        /// </summary>
        [JsonPropertyName("rewardDisabled")]
        public bool RewardDisabled { get; set; }

        /// <summary>Why the site was disabled (free text, diagnostics only).</summary>
        [JsonPropertyName("disabledReason")]
        public string DisabledReason { get; set; }

        /// <summary>May a run pick this site as a gate puzzle?</summary>
        [JsonIgnore]
        public bool UsableAsGate => !Disabled;

        /// <summary>May a run use this site for its reward scene (the populate-time fallback or the arming choice)?</summary>
        [JsonIgnore]
        public bool UsableAsReward => !Disabled && !RewardDisabled;
    }
}
