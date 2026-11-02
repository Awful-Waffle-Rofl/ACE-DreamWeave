using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

using ACE.Server.HazardMechanics;
using ACE.Server.ThreadDungeons.Defs;

namespace ACE.Server.SpeedCourses
{
    /// <summary>
    /// Root of Content/dungeons/dynamic/speed-courses.json. Optional, like puzzle-gates.json: a missing or
    /// malformed file means "no speed courses", never a refused server. One course per seasonId. Courses are kept as raw
    /// JsonElements so one wrongly typed course costs only that course (the puzzle-gates.json convention).
    /// </summary>
    public class SpeedCoursesFileDef
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("courses")]
        public List<JsonElement> Courses { get; set; } = new List<JsonElement>();
    }

    /// <summary>The slots a puzzle can occupy.</summary>
    public static class SpeedPuzzleSlots
    {
        public const string P1 = "P1";
        public const string P2 = "P2";
        public const string P3 = "P3";

        public static readonly string[] All = { P1, P2, P3 };
    }

    /// <summary>The slots a named creature can occupy.</summary>
    public static class SpeedNamedSlots
    {
        public const string K1 = "K1";
        public const string K2 = "K2";
        public const string Boss = "BOSS";

        public static readonly string[] All = { K1, K2, Boss };
    }

    /// <summary>The hazard trigger kinds (the strings used in the file).</summary>
    public static class SpeedHazardTriggers
    {
        public const string ClockRebase = "clock_rebase";
        public const string WhileInRegion = "while_in_region";
        public const string OnNamedDeadAndInRegion = "on_named_dead_and_in_region";
        public const string BossAliveInterval = "boss_alive_interval";
        public const string BossHealthBelow = "boss_health_below";

        /// <summary>
        /// Started only by the course's boss phases (<see cref="SpeedCourseDef.BossPhases"/>), as one step of a phase; the
        /// trigger state machine never starts or stops it (SpeedCourseRules.DecideHazard holds it).
        /// </summary>
        public const string BossPhase = "boss_phase";

        public static readonly string[] All =
        {
            ClockRebase, WhileInRegion, OnNamedDeadAndInRegion, BossAliveInterval, BossHealthBelow, BossPhase,
        };
    }

    /// <summary>One speed course, bound to one speed season.</summary>
    public class SpeedCourseDef
    {
        [JsonPropertyName("seasonId")]
        public int SeasonId { get; set; }

        /// <summary>A Threads index.json dungeon row id, e.g. "speed_black_book".</summary>
        [JsonPropertyName("dungeonId")]
        public string DungeonId { get; set; }

        /// <summary>Species-table family id, e.g. "undead".</summary>
        [JsonPropertyName("family")]
        public string Family { get; set; }

        /// <summary>The season seed: fixes the Threads draw and every hazard seed.</summary>
        [JsonPropertyName("seed")]
        public int Seed { get; set; }

        [JsonPropertyName("tier")]
        public int Tier { get; set; } = 1;

        /// <summary>Course-level floor of the run level; the effective floor is max(season, course, 1).</summary>
        [JsonPropertyName("levelFloor")]
        public int LevelFloor { get; set; }

        /// <summary>Flat run level: when above 0 every run plays at this level whatever the player's level (the floors then do not apply); 0 scales to the player.</summary>
        [JsonPropertyName("fixedLevel")]
        public int FixedLevel { get; set; }

        /// <summary>The ObjectiveLockKey of the Seal gate, e.g. "bb_seal".</summary>
        [JsonPropertyName("sealKey")]
        public string SealKey { get; set; }

        /// <summary>Total token weight the Seal needs, e.g. 8.</summary>
        [JsonPropertyName("sealRequired")]
        public double SealRequired { get; set; }

        /// <summary>Token name -> weight, e.g. p1 3, p3 3, north_rite 1.</summary>
        [JsonPropertyName("tokens")]
        public Dictionary<string, double> Tokens { get; set; } = new Dictionary<string, double>();

        [JsonPropertyName("puzzles")]
        public List<SpeedPuzzleDef> Puzzles { get; set; } = new List<SpeedPuzzleDef>();

        [JsonPropertyName("named")]
        public List<SpeedNamedDef> Named { get; set; } = new List<SpeedNamedDef>();

        [JsonPropertyName("hazards")]
        public List<SpeedHazardDef> Hazards { get; set; } = new List<SpeedHazardDef>();

        /// <summary>
        /// Door guardians, separate from <see cref="Named"/>: each is a clone of a plan creature placed at its own point,
        /// and for each key in <see cref="SpeedGuardianDef.DoorKeys"/>, once every guardian naming that key is dead (or could
        /// not be fielded), the run contributes once to the objective door keyed that way (SpeedCourseRun). Empty: the
        /// course has none.
        /// </summary>
        [JsonPropertyName("guardians")]
        public List<SpeedGuardianDef> Guardians { get; set; } = new List<SpeedGuardianDef>();

        /// <summary>
        /// ObjectiveLockKeys of gate Doors a legitimate run may leave shut (the open-order corner rooms: a run needs three
        /// of the four). The speed finish backstop (Player.CheckRequiredGates) skips gates with these keys instead of
        /// forfeiting the run. Never the sealKey. Empty: every objective-gated Door is required.
        /// </summary>
        [JsonPropertyName("optionalGateKeys")]
        public List<string> OptionalGateKeys { get; set; } = new List<string>();

        /// <summary>
        /// The boss's immunity phases, in order (SpeedBossPhaseMachine). Empty: the boss has no guard. Thresholds strictly
        /// descending in (0, 1]; each phase's hazards are boss_phase hazards run one after another while the boss is immune.
        /// </summary>
        [JsonPropertyName("bossPhases")]
        public List<SpeedBossPhaseDef> BossPhases { get; set; } = new List<SpeedBossPhaseDef>();

        /// <summary>
        /// Optional: the Seal door shuts behind the runner when they stand in this region with the boss alive, and opens
        /// again when the boss dies or the run ends (SpeedCourseRules.DecideSealDoor). Null: the door is never shut.
        /// </summary>
        [JsonPropertyName("sealLock")]
        public SpeedSealLockDef SealLock { get; set; }

        /// <summary>Token -> the name the runner is told, e.g. p1 -> "First ward gate". A token without one is told by its id.</summary>
        [JsonPropertyName("labels")]
        public Dictionary<string, string> Labels { get; set; } = new Dictionary<string, string>();

        /// <summary>The Seal progress the runner is told after each token, group by group ("ward gates 1 of 2, rites 0 of 2").</summary>
        [JsonPropertyName("progressGroups")]
        public List<SpeedProgressGroupDef> ProgressGroups { get; set; } = new List<SpeedProgressGroupDef>();

        /// <summary>What the runner is told. Null, or a null line: that line is not sent.</summary>
        [JsonPropertyName("messages")]
        public SpeedCourseMessagesDef Messages { get; set; }

        /// <summary>The weight of <paramref name="token"/>, or 0 when the course does not define it.</summary>
        public double WeightOf(string token)
            => token != null && Tokens != null && Tokens.TryGetValue(token, out var w) ? w : 0.0;

        /// <summary>The label of <paramref name="token"/>, or the token itself.</summary>
        public string LabelOf(string token)
            => token != null && Labels != null && Labels.TryGetValue(token, out var l) && !string.IsNullOrWhiteSpace(l) ? l : token;
    }

    /// <summary>One counted group of Seal tokens, e.g. "rites": north_rite, east_rite, south_rite, of 2.</summary>
    public class SpeedProgressGroupDef
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("tokens")]
        public List<string> Tokens { get; set; } = new List<string>();

        /// <summary>How many of the group the Seal needs; the told count is capped here.</summary>
        [JsonPropertyName("of")]
        public int Of { get; set; }
    }

    /// <summary>
    /// The runner's lines. Templates take {label} (the token's label) and {groups} (the progress groups, e.g. "ward
    /// gates 1 of 2, rites 0 of 2"). All are sent as system chat to the runner only.
    /// </summary>
    public class SpeedCourseMessagesDef
    {
        /// <summary>Sent once when the run's clock is rebased (the course starts).</summary>
        [JsonPropertyName("intro")]
        public string Intro { get; set; }

        /// <summary>Sent once per token earned, e.g. "{label} complete. Seal: {groups}."</summary>
        [JsonPropertyName("progress")]
        public string Progress { get; set; }

        /// <summary>Sent instead of <see cref="Progress"/> for a token the run granted because the course could not offer it.</summary>
        [JsonPropertyName("granted")]
        public string Granted { get; set; }

        /// <summary>Sent once when the Seal latches.</summary>
        [JsonPropertyName("sealOpen")]
        public string SealOpen { get; set; }

        /// <summary>
        /// Sent once per guardian door the runner opens by killing its guardians. Not sent for a door the run opened
        /// because it could not field a guardian (that is logged instead).
        /// </summary>
        [JsonPropertyName("guardianDoor")]
        public string GuardianDoor { get; set; }
    }

    /// <summary>One door guardian of a course (<see cref="SpeedCourseDef.Guardians"/>).</summary>
    public class SpeedGuardianDef
    {
        /// <summary>Unique within the course; the spawned creature's stamp slot (log text).</summary>
        [JsonPropertyName("id")]
        public string Id { get; set; }

        /// <summary>
        /// The ObjectiveLockKeys of the doors this guardian holds shut (the Black Book's Cell Keepers: both doors of their
        /// room). Each key opens once every guardian naming it is down; several guardians may share a key. Non-empty,
        /// unique, never the sealKey. (Migrated from a single "doorKey" 2026-10-25: a file still carrying only that
        /// field reads as an empty list and the course is rejected, never silently half-wired.)
        /// </summary>
        [JsonPropertyName("doorKeys")]
        public List<string> DoorKeys { get; set; } = new List<string>();

        /// <summary>Cell, x y z and quaternion (qw first), the same shape as a named slot's position.</summary>
        [JsonPropertyName("position")]
        public DungeonSpawnPointDef Position { get; set; }

        [JsonPropertyName("displayName")]
        public string DisplayName { get; set; }

        /// <summary>A runtime scale multiplier on the spawned creature, as <see cref="SpeedNamedDef.Scale"/>. Must be in (0, 10].</summary>
        [JsonPropertyName("scale")]
        public float Scale { get; set; } = 1f;
    }

    /// <summary>One puzzle slot of a course.</summary>
    public class SpeedPuzzleDef
    {
        /// <summary>"P1" | "P2" | "P3".</summary>
        [JsonPropertyName("slot")]
        public string Slot { get; set; }

        /// <summary>A puzzle site id of the course's dungeon (puzzle-gates.json).</summary>
        [JsonPropertyName("siteId")]
        public string SiteId { get; set; }

        /// <summary>The Seal token a solve contributes. Null for P2, which contributes nothing.</summary>
        [JsonPropertyName("token")]
        public string Token { get; set; }

        [JsonPropertyName("rounds")]
        public int Rounds { get; set; } = 1;

        /// <summary>Seconds a wrong answer locks the puzzle out.</summary>
        [JsonPropertyName("lockoutSeconds")]
        public double LockoutSeconds { get; set; } = 5;
    }

    /// <summary>A named creature slot of a course: where it stands and what it contributes.</summary>
    public class SpeedNamedDef
    {
        /// <summary>"K1" | "K2" | "BOSS".</summary>
        [JsonPropertyName("slot")]
        public string Slot { get; set; }

        /// <summary>Cell, x y z and quaternion (qw first), the same shape as a Threads spawn point.</summary>
        [JsonPropertyName("position")]
        public DungeonSpawnPointDef Position { get; set; }

        /// <summary>
        /// The Seal token a kill contributes. K1 = north_rite; null for the others. The RUN contributes it when it sees
        /// the creature dead (the creature itself is not an objective contributor), so the runner hears the course's
        /// own line, never the generic objective one.
        /// </summary>
        [JsonPropertyName("token")]
        public string Token { get; set; }

        /// <summary>
        /// A puzzle slot (P1/P2/P3). Set: the creature is held out of the instance until that puzzle is solved (or the
        /// run gives the puzzle up, which leaves the way open), the way the boss is held for the Seal. K1 behind P2
        /// cannot be reached, by any means, before the gate opens.
        /// </summary>
        [JsonPropertyName("holdUntilSolved")]
        public string HoldUntilSolved { get; set; }

        [JsonPropertyName("displayName")]
        public string DisplayName { get; set; }

        /// <summary>
        /// A runtime scale multiplier on the spawned creature (Creature.ApplyRuntimeScaleMultiplier, before EnterWorld):
        /// 1 leaves the weenie's own size, 4 makes the boss four times as large. Must be in (0, 10].
        /// </summary>
        [JsonPropertyName("scale")]
        public float Scale { get; set; } = 1f;
    }

    /// <summary>
    /// One boss immunity phase. The phase starts when the boss's health is at or below its threshold floor,
    /// ceil(max x <see cref="AtFraction"/>), and the runner is in <see cref="TriggerRegion"/> (when set). While it runs the
    /// boss is immune and its health is pinned at that floor; its <see cref="Hazards"/> run one after another, and when
    /// the last has ended (or <see cref="TimeoutSeconds"/> passed) the boss is vulnerable down to the next phase's floor.
    /// </summary>
    public class SpeedBossPhaseDef
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }

        /// <summary>The health fraction the phase starts at, in (0, 1]. 1.0 is the entry phase (immune from spawn).</summary>
        [JsonPropertyName("atFraction")]
        public double AtFraction { get; set; }

        /// <summary>boss_phase hazard ids, run in this order.</summary>
        [JsonPropertyName("hazards")]
        public List<string> Hazards { get; set; } = new List<string>();

        /// <summary>The failsafe: the phase ends this many seconds after it started, whatever its hazards are doing.</summary>
        [JsonPropertyName("timeoutSeconds")]
        public double TimeoutSeconds { get; set; }

        /// <summary>Optional: the phase starts only while the runner is inside this area. Null: no runner condition.</summary>
        [JsonPropertyName("triggerRegion")]
        public SpeedHazardRegionDef TriggerRegion { get; set; }
    }

    /// <summary>The Seal door lock of a course (<see cref="SpeedCourseDef.SealLock"/>).</summary>
    public class SpeedSealLockDef
    {
        /// <summary>The door shuts only while the runner is inside this area (the hub the Seal door leads into).</summary>
        [JsonPropertyName("region")]
        public SpeedHazardRegionDef Region { get; set; }
    }

    /// <summary>A hazard region as authored: a rect or a circle at a fixed ground plane.</summary>
    public class SpeedHazardRegionDef
    {
        /// <summary>"rect" | "circle".</summary>
        [JsonPropertyName("shape")]
        public string Shape { get; set; } = "rect";

        [JsonPropertyName("minX")] public float MinX { get; set; }
        [JsonPropertyName("maxX")] public float MaxX { get; set; }
        [JsonPropertyName("minY")] public float MinY { get; set; }
        [JsonPropertyName("maxY")] public float MaxY { get; set; }

        [JsonPropertyName("centerX")] public float CenterX { get; set; }
        [JsonPropertyName("centerY")] public float CenterY { get; set; }
        [JsonPropertyName("radius")] public float Radius { get; set; }

        /// <summary>The ground plane z (dungeon regions are always Plane mode).</summary>
        [JsonPropertyName("groundZ")]
        public float GroundZ { get; set; }

        /// <summary>Ceiling face z, or null for no ceiling.</summary>
        [JsonPropertyName("ceilingZ")]
        public float? CeilingZ { get; set; }

        /// <summary>Fixed z for moving hazards, or null: they fly at ground + <see cref="HazardOffset"/>.</summary>
        [JsonPropertyName("hazardPlaneZ")]
        public float? HazardPlaneZ { get; set; }

        [JsonPropertyName("hazardOffset")]
        public float HazardOffset { get; set; } = 0.5f;

        /// <summary>A player counts as in the region from groundZ - bandBelow ...</summary>
        [JsonPropertyName("bandBelow")]
        public float BandBelow { get; set; } = 1f;

        /// <summary>... to groundZ + bandAbove.</summary>
        [JsonPropertyName("bandAbove")]
        public float BandAbove { get; set; } = 4f;

        /// <summary>
        /// Optional placement exclusions (circles): nothing a hazard places lands inside one (the south chute sits
        /// inside the south terminal's memory rect). A placement rule only: a player standing in one still counts as in
        /// the region (<see cref="Contains"/> ignores them, as HazardRegion does). Null or empty: none.
        /// </summary>
        [JsonPropertyName("exclusions")]
        public List<SpeedHazardExclusionDef> Exclusions { get; set; }

        /// <summary>The exclusions as HazardRegion takes them; empty when none are authored.</summary>
        public IReadOnlyList<HazardExclusion> BuildExclusions()
        {
            var list = new List<HazardExclusion>();

            if (Exclusions != null)
                foreach (var e in Exclusions)
                    if (e != null)
                        list.Add(HazardExclusion.Circle(e.CenterX, e.CenterY, e.Radius));

            return list;
        }

        /// <summary>Builds the region for a run's landblock and instance (the landblock is the dungeon row's).</summary>
        public HazardRegion Build(string name, ushort landblock, uint instance)
        {
            var exclusions = BuildExclusions();

            if (string.Equals(Shape, "circle", StringComparison.OrdinalIgnoreCase))
                return HazardRegion.Circle(name, landblock, instance, false, CenterX, CenterY, Radius,
                    HazardGroundMode.Plane, GroundZ, HazardPlaneZ, HazardOffset, CeilingZ, BandBelow, BandAbove, exclusions);

            return HazardRegion.Rect(name, landblock, instance, false, MinX, MaxX, MinY, MaxY,
                HazardGroundMode.Plane, GroundZ, HazardPlaneZ, HazardOffset, CeilingZ, BandBelow, BandAbove, exclusions);
        }

        /// <summary>True when (x, y, z) is inside the 2D shape and the z band. Pure; mirrors the region's own test for a Plane region.</summary>
        public bool Contains(float x, float y, float z)
        {
            if (z < GroundZ - BandBelow || z > GroundZ + BandAbove)
                return false;

            if (string.Equals(Shape, "circle", StringComparison.OrdinalIgnoreCase))
            {
                var dx = x - CenterX;
                var dy = y - CenterY;
                return dx * dx + dy * dy <= Radius * Radius;
            }

            return x >= Math.Min(MinX, MaxX) && x <= Math.Max(MinX, MaxX) && y >= Math.Min(MinY, MaxY) && y <= Math.Max(MinY, MaxY);
        }
    }

    /// <summary>One circular placement exclusion of a hazard region, in landblock-local metres.</summary>
    public class SpeedHazardExclusionDef
    {
        [JsonPropertyName("centerX")] public float CenterX { get; set; }
        [JsonPropertyName("centerY")] public float CenterY { get; set; }
        [JsonPropertyName("radius")] public float Radius { get; set; }
    }

    /// <summary>Where a hazard is centred (<see cref="SpeedHazardDef.Origin"/>).</summary>
    public static class SpeedHazardOrigins
    {
        /// <summary>The region's own centre (the mechanic's default).</summary>
        public const string Region = "region";

        /// <summary>The boss's position at each start, passed as args cx/cy.</summary>
        public const string Boss = "boss";

        public static readonly string[] All = { Region, Boss };
    }

    /// <summary>When a hazard starts and stops. See SpeedCourseRules.DecideHazard.</summary>
    public class SpeedHazardTriggerDef
    {
        /// <summary>One of <see cref="SpeedHazardTriggers.All"/>.</summary>
        [JsonPropertyName("type")]
        public string Type { get; set; }

        /// <summary>on_named_dead_and_in_region: the named slot (K1/K2/BOSS) that must be dead.</summary>
        [JsonPropertyName("slot")]
        public string Slot { get; set; }

        /// <summary>boss_alive_interval: seconds between starts.</summary>
        [JsonPropertyName("seconds")]
        public double Seconds { get; set; }

        /// <summary>boss_health_below: health fraction (0..1) at or below which the hazard starts.</summary>
        [JsonPropertyName("fraction")]
        public double Fraction { get; set; }
    }

    /// <summary>One hazard of a course.</summary>
    public class SpeedHazardDef
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }

        /// <summary>A startable hazard mechanic kind: wisps | balls | memory | zones.</summary>
        [JsonPropertyName("kind")]
        public string Kind { get; set; }

        /// <summary>The mechanic's pattern; null/empty takes the mechanic's default.</summary>
        [JsonPropertyName("pattern")]
        public string Pattern { get; set; }

        [JsonPropertyName("region")]
        public SpeedHazardRegionDef Region { get; set; }

        /// <summary>
        /// Optional: the area the trigger's "runner in region" test reads, when it should differ from where the hazard
        /// plays (<see cref="Region"/>). The east hazards play in their rooms but start only once the runner is inside the
        /// whole east arm, and stop when they leave it. Null: <see cref="Region"/>.
        /// </summary>
        [JsonPropertyName("triggerRegion")]
        public SpeedHazardRegionDef TriggerRegion { get; set; }

        /// <summary>The area the trigger tests the runner against: <see cref="TriggerRegion"/>, else <see cref="Region"/>.</summary>
        public SpeedHazardRegionDef TriggerArea => TriggerRegion ?? Region;

        [JsonPropertyName("trigger")]
        public SpeedHazardTriggerDef Trigger { get; set; }

        /// <summary>key=value settings, passed through as the hazard settings layer (HazardArgs).</summary>
        [JsonPropertyName("args")]
        public Dictionary<string, string> Args { get; set; } = new Dictionary<string, string>();

        /// <summary>The pct authored at the benchmark; scaled to the run level by HazardLevelScale.Scaled. 0 = the hazard has no pct.</summary>
        [JsonPropertyName("pctAuthored")]
        public double PctAuthored { get; set; }

        [JsonPropertyName("pctMin")]
        public double PctMin { get; set; }

        [JsonPropertyName("pctMax")]
        public double PctMax { get; set; }

        /// <summary>Seal token contributed when the hazard run ends Completed (south memory = south_rite). Null: none.</summary>
        [JsonPropertyName("token")]
        public string Token { get; set; }

        /// <summary>After a Failed end, seconds to wait before restarting with a new seed. 0 = restart at once.</summary>
        [JsonPropertyName("retrySeconds")]
        public double RetrySeconds { get; set; }

        /// <summary>
        /// "region" | "boss" | null. Null takes "boss" for a boss_alive_interval trigger (the boss fans) and "region"
        /// otherwise. "boss": each start passes the boss's current position as args cx/cy, which the balls patterns
        /// (fan origin, ring centre) and the wisps ring/diagonal read; a pattern that reads no centre ignores it.
        /// </summary>
        [JsonPropertyName("origin")]
        public string Origin { get; set; }

        /// <summary>True when each start is centred on the boss (see <see cref="Origin"/>).</summary>
        public bool OriginAtBoss
            => Origin == null
                ? string.Equals(Trigger?.Type, SpeedHazardTriggers.BossAliveInterval, StringComparison.Ordinal)
                : string.Equals(Origin, SpeedHazardOrigins.Boss, StringComparison.Ordinal);

        /// <summary>The settings layer for HazardManager.Start: <see cref="Args"/> as HazardArgs.</summary>
        public HazardArgs ToHazardArgs()
        {
            var tokens = new List<string>();

            if (Args != null)
                foreach (var kv in Args)
                    if (!string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrEmpty(kv.Value))
                        tokens.Add(kv.Key + "=" + kv.Value);

            return HazardArgs.FromTokens(tokens);
        }
    }
}