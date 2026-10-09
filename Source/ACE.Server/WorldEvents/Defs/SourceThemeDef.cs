using System.Collections.Generic;
using System.Text.Json.Serialization;

using ACE.Server.WorldEvents;

namespace ACE.Server.WorldEvents.Defs
{
    /// <summary>
    /// Spawn geometry a source theme places its wave positions on. Parsed case-insensitively
    /// from SourceThemeDef.Geometry.
    /// </summary>
    public enum SourceGeometry
    {
        Ring,
        Edges,
        Single,

        /// <summary>
        /// geometryPoints positions sampled uniformly BY AREA inside the disc of geometryRadius around the
        /// anchor, RESAMPLED every wave rather than fixed at Stage (WP-16, TECH-DESIGN 2.4). Sources and the
        /// boss still use the Stage-time sample.
        /// </summary>
        Disc
    }

    /// <summary>
    /// One entry from Content/events/axes/sources.json (TECH-DESIGN 5.4).
    /// </summary>
    public class SourceThemeDef
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("displayName")]
        public string DisplayName { get; set; }

        [JsonPropertyName("geometry")]
        public string Geometry { get; set; }

        [JsonPropertyName("geometryRadius")]
        public float GeometryRadius { get; set; }

        /// <summary>
        /// The raw optional <c>clearanceRadius</c> key from sources.json. Null when the key is absent. Read
        /// <see cref="ClearanceRadius"/> instead; this exists only so the loader can tell "absent" from "0".
        /// </summary>
        [JsonPropertyName("clearanceRadius")]
        public float? ClearanceRadiusOverride { get; set; }

        /// <summary>
        /// The ground clearance (metres) a town spot must have to host this source: what the admin start
        /// page compares against a spot's max_radius and what <c>WorldEventAdminService.EdgeMarginFor</c>
        /// uses as the radius term. Defaults to <see cref="GeometryRadius"/> when the key is absent, so only
        /// a source that sets it explicitly (sky_rift: its decor floats overhead and collides with nothing)
        /// behaves differently. It does NOT change where spawns are placed - geometryRadius still does that.
        /// </summary>
        [JsonIgnore]
        public float ClearanceRadius => ClearanceRadiusOverride ?? GeometryRadius;

        [JsonPropertyName("geometryPoints")]
        public int GeometryPoints { get; set; }

        [JsonPropertyName("waveIntervalSeconds")]
        public double WaveIntervalSeconds { get; set; }

        [JsonPropertyName("maxAlive")]
        public int MaxAlive { get; set; }

        [JsonPropertyName("waveCount")]
        public ScaledCount WaveCount { get; set; }

        [JsonPropertyName("holdAdjacentLandblocks")]
        public bool HoldAdjacentLandblocks { get; set; }

        /// <summary>
        /// Overflow champions (TECH-DESIGN 2.15): how much UNPLACEABLE wave demand - the difference between
        /// the uncapped wave size and the size the cap and MaxAlive actually allowed - buys one extra
        /// role-2 champion. A turnout big enough to saturate the trash cap therefore gets harder monsters
        /// rather than simply a wasted estimate.
        ///
        /// 0 or less disables overflow champions for the theme. Defaults to
        /// <see cref="DefaultOverflowPerChampion"/> when the key is absent, so a theme that never heard of
        /// this feature still gets the shipped behaviour.
        /// </summary>
        [JsonPropertyName("overflowPerChampion")]
        public int OverflowPerChampion { get; set; } = DefaultOverflowPerChampion;

        /// <summary>
        /// The ceiling on how many overflow champions may be alive at once. They are deliberately NOT
        /// charged against <see cref="MaxAlive"/> - they are additive above it - so this is the only thing
        /// bounding them.
        /// </summary>
        [JsonPropertyName("overflowChampionMaxAlive")]
        public int OverflowChampionMaxAlive { get; set; } = DefaultOverflowChampionMaxAlive;

        public const int DefaultOverflowPerChampion = 4;
        public const int DefaultOverflowChampionMaxAlive = 3;

        /// <summary>
        /// WP-24 (owner ruling 2026-08-16): the health multiplier a SYNTHETIC elite gets - a role-0 member
        /// promoted into the every-third-wave elite slot because the elite band had no real role-1 member -
        /// on top of the wave's ordinary healthMult (crowd x pace). Defaults to
        /// <see cref="DefaultSyntheticEliteHealthMult"/> when the key is absent, so an older theme JSON with
        /// no opinion here still gets the shipped behaviour.
        /// </summary>
        [JsonPropertyName("syntheticEliteHealthMult")]
        public double SyntheticEliteHealthMult { get; set; } = DefaultSyntheticEliteHealthMult;

        /// <summary>
        /// WP-24: the health multiplier a SYNTHETIC overflow champion gets - a role-0 member promoted
        /// because the elite band had no real role-2/role-1 member - on top of the wave's ordinary
        /// healthMult. Defaults to <see cref="DefaultSyntheticChampionHealthMult"/> when absent.
        /// </summary>
        [JsonPropertyName("syntheticChampionHealthMult")]
        public double SyntheticChampionHealthMult { get; set; } = DefaultSyntheticChampionHealthMult;

        public const double DefaultSyntheticEliteHealthMult = 2.0;
        public const double DefaultSyntheticChampionHealthMult = 3.0;

        /// <summary>
        /// Crowd health scaling (TECH-DESIGN 2.15). Never null after a load: the axis store replaces a
        /// null (an explicit "crowdHealth": null in JSON) with defaults.
        /// </summary>
        [JsonPropertyName("crowdHealth")]
        public CrowdHealthDef CrowdHealth { get; set; } = new CrowdHealthDef();

        /// <summary>
        /// Real-time pace controller tunables (TECH-DESIGN 2.15). Never null after a load, for the same
        /// reason as <see cref="CrowdHealth"/>.
        /// </summary>
        [JsonPropertyName("pace")]
        public PaceDef Pace { get; set; } = new PaceDef();

        /// <summary>
        /// <see cref="WaveIntervalSeconds"/>, or world_events_wave_interval_seconds when that property is
        /// non-zero (live global override, TECH-DESIGN "Live overrides"). Read fresh on every access - see
        /// <see cref="WorldEventOverrides"/>.
        /// </summary>
        [JsonIgnore]
        public double EffectiveWaveIntervalSeconds => WorldEventOverrides.Double("world_events_wave_interval_seconds", WaveIntervalSeconds);

        /// <summary><see cref="MaxAlive"/>, or world_events_max_alive when non-zero.</summary>
        [JsonIgnore]
        public int EffectiveMaxAlive => (int)WorldEventOverrides.Long("world_events_max_alive", MaxAlive);

        /// <summary>
        /// <see cref="WaveCount"/>.Base, or world_events_wave_count_base when non-zero. NOT applied to
        /// <see cref="ScaledCount"/> itself - see that class's remarks on why the override lives at this
        /// call site instead of on the shared type.
        /// </summary>
        [JsonIgnore]
        public int EffectiveWaveCountBase => WaveCount == null ? 0 : (int)WorldEventOverrides.Long("world_events_wave_count_base", WaveCount.Base);

        /// <summary><see cref="WaveCount"/>.PerParticipant, or world_events_wave_count_per_participant when non-zero.</summary>
        [JsonIgnore]
        public double EffectiveWaveCountPerParticipant => WaveCount == null ? 0 : WorldEventOverrides.Double("world_events_wave_count_per_participant", WaveCount.PerParticipant);

        /// <summary><see cref="WaveCount"/>.Cap, or world_events_wave_count_cap when non-zero.</summary>
        [JsonIgnore]
        public int EffectiveWaveCountCap => WaveCount == null ? 0 : (int)WorldEventOverrides.Long("world_events_wave_count_cap", WaveCount.Cap);

        /// <summary>
        /// <see cref="ScaledCount.Resolve(int, double, int, int)"/> against the effective waveCount dials
        /// (<see cref="EffectiveWaveCountBase"/>/<see cref="EffectiveWaveCountPerParticipant"/>/
        /// <see cref="EffectiveWaveCountCap"/>), i.e. what <c>WaveCount.Resolve(participants)</c> would
        /// return with any live override applied. 0 when <see cref="WaveCount"/> is null (never true after a
        /// store load - <see cref="WorldEventAxisStore"/> drops a theme with no waveCount - but this stays
        /// safe for a hand-built test theme).
        /// </summary>
        public int ResolveWaveCount(int participants)
        {
            return WaveCount == null ? 0 : ScaledCount.Resolve(EffectiveWaveCountBase, EffectiveWaveCountPerParticipant, EffectiveWaveCountCap, participants);
        }

        /// <summary>The uncapped counterpart of <see cref="ResolveWaveCount"/> - see <see cref="ScaledCount.ResolveRaw(int)"/>.</summary>
        public int ResolveWaveCountRaw(int participants)
        {
            return WaveCount == null ? 0 : ScaledCount.ResolveRaw(EffectiveWaveCountBase, EffectiveWaveCountPerParticipant, participants);
        }

        /// <summary><see cref="OverflowPerChampion"/>, or world_events_overflow_per_champion when non-zero.</summary>
        [JsonIgnore]
        public int EffectiveOverflowPerChampion => (int)WorldEventOverrides.Long("world_events_overflow_per_champion", OverflowPerChampion);

        /// <summary><see cref="SyntheticEliteHealthMult"/>, or world_events_synthetic_elite_health_mult when non-zero.</summary>
        [JsonIgnore]
        public double EffectiveSyntheticEliteHealthMult => WorldEventOverrides.Double("world_events_synthetic_elite_health_mult", SyntheticEliteHealthMult);

        /// <summary><see cref="SyntheticChampionHealthMult"/>, or world_events_synthetic_champion_health_mult when non-zero.</summary>
        [JsonIgnore]
        public double EffectiveSyntheticChampionHealthMult => WorldEventOverrides.Double("world_events_synthetic_champion_health_mult", SyntheticChampionHealthMult);

        /// <summary>
        /// The objective creature this theme places at EVERY geometry anchor the moment the run goes Active -
        /// the "rift" theme's Rift (wcid 1002603). 0, the default and the "ambush" theme's value, means the
        /// theme places no objective spawn at all.
        /// </summary>
        [JsonPropertyName("objectiveWcid")]
        public uint ObjectiveWcid { get; set; }

        /// <summary>
        /// Fixed-offset objective (Source) spawns (WP-21). Empty, the default and every pre-WP-21 theme's
        /// value, means the theme places its objectives at the geometry anchors via
        /// <see cref="ObjectiveWcid"/> exactly as before. When this list is non-empty it REPLACES that
        /// anchor-following placement entirely - see ObjectiveDef's remarks. Entries that fail validation
        /// are dropped individually at load; the theme itself is always kept.
        /// </summary>
        [JsonPropertyName("objectives")]
        public List<ObjectiveDef> Objectives { get; set; } = new List<ObjectiveDef>();

        /// <summary>
        /// Scales objective (Source) creature max health with the run's estimated participant count
        /// (WP-21), the same ScaledCount shape as <see cref="WaveCount"/>. Null, the default and every
        /// pre-WP-21 theme's value, leaves the weenie's shipped health alone. Applies to EVERY Source-kind
        /// spawn - both anchor-placed (ObjectiveWcid) and fixed-offset (Objectives).
        /// </summary>
        [JsonPropertyName("objectiveHealth")]
        public ScaledCount ObjectiveHealth { get; set; }

        [JsonPropertyName("rewardRadius")]
        public float RewardRadius { get; set; }

        [JsonPropertyName("compatibleGoals")]
        public List<string> CompatibleGoals { get; set; } = new List<string>();

        /// <summary>
        /// Whether the web Admin panel may start this source (Docs/AdminPanel/WORLD-EVENTS-START.md, owner
        /// ruling 3). True when the key is absent, so every theme that predates the flag stays startable.
        /// "ambush" ships false: its 45 m edge geometry fits almost no in-town spot. The web catalog omits a
        /// false source, the web start and preview routes refuse it, and a web random start never resolves
        /// to it. In-game "/worldevent start" ignores this flag entirely.
        /// </summary>
        [JsonPropertyName("webStartable")]
        public bool WebStartable { get; set; } = true;

        /// <summary>
        /// Optional per-source override for the GOAL display name used in the run announcements (the
        /// active line and every outcome line). Null - the default, and every theme's value but the rift
        /// and the five element portals - means the goal's own DisplayName is used unchanged.
        ///
        /// This is what lets ONE goal id serve several sources with the right words: destroy_source reads
        /// as "Destroy the Rifts" on the rift theme and "Shatter the Pillars" on an element portal, with no
        /// second goal id behind it. An empty or whitespace value is repaired to null at load.
        ///
        /// Applied ONLY when the composed goal's <see cref="GoalType"/> is <see cref="GoalType.DestroySource"/>
        /// (2026-08-19) - see <see cref="ACE.Server.WorldEvents.WorldEventComposition.GoalDisplayName"/>. Under
        /// kill_count/kill_boss the goal's own name always wins, even on a theme that declares this field.
        /// </summary>
        [JsonPropertyName("goalDisplayName")]
        public string GoalDisplayName { get; set; }

        /// <summary>
        /// Optional per-source override for the Success outcome-line template (2026-08-19). Same
        /// DestroySource-only scoping as <see cref="GoalDisplayName"/> - see
        /// <see cref="ACE.Server.WorldEvents.WorldEventComposition.SuccessTemplate"/>. Null means the goal's
        /// own <see cref="GoalDef.SuccessTemplate"/> (if any) stands.
        /// </summary>
        [JsonPropertyName("successTemplate")]
        public string SuccessTemplate { get; set; }

        /// <summary>
        /// Optional per-source override for every Failed* outcome-line template (2026-08-19). Same
        /// DestroySource-only scoping as <see cref="GoalDisplayName"/>.
        /// </summary>
        [JsonPropertyName("failTemplate")]
        public string FailTemplate { get; set; }

        /// <summary>
        /// What ONE of this theme's objective spawns is called in player-facing text - "rift" by default,
        /// "pillar" on the element portals. Substituted into a goal progressTemplate's {noun} token and
        /// into the DestroySource MVP sentence. An empty or whitespace value is repaired to the default at
        /// load, so this is never null after a parse.
        /// </summary>
        [JsonPropertyName("objectiveNoun")]
        public string ObjectiveNoun { get; set; } = DefaultObjectiveNoun;

        /// <summary>
        /// The plural of <see cref="ObjectiveNoun"/>, carried explicitly rather than derived - English
        /// plurals are not a rule a content file should have to obey. Substituted into a goal
        /// progressTemplate's {nounPlural} token. Repaired to the default exactly as ObjectiveNoun is.
        /// </summary>
        [JsonPropertyName("objectiveNounPlural")]
        public string ObjectiveNounPlural { get; set; } = DefaultObjectiveNounPlural;

        public const string DefaultObjectiveNoun = "rift";
        public const string DefaultObjectiveNounPlural = "rifts";

        [JsonPropertyName("startFlavour")]
        public string StartFlavour { get; set; }

        [JsonPropertyName("waveFlavour")]
        public string WaveFlavour { get; set; }

        /// <summary>
        /// Optional per-theme override for the pre-event teaser broadcast (WorldEventAnnouncer.TeaserLine).
        /// Supports {anchor}. Null/blank - every shipped theme - falls back to the shipped generic teaser
        /// pool (WorldEventAnnouncer.TeaserFlavours).
        /// </summary>
        [JsonPropertyName("teaserFlavour")]
        public string TeaserFlavour { get; set; }

        /// <summary>
        /// Spawn-time scenery placed once at the geometry CENTRE when the run goes Active (WP-14). Empty,
        /// the default and both v1 themes' value, means the theme places no decor at all. Entries that fail
        /// validation are dropped individually at load; the theme itself is always kept.
        /// </summary>
        [JsonPropertyName("decor")]
        public List<DecorDef> Decor { get; set; } = new List<DecorDef>();

        /// <summary>
        /// Random color variants for <see cref="Decor"/> (WP-25). Each entry is ONE color style: a wcid
        /// substitution list applied to Decor by DISTINCT-wcid order of first appearance - style[0] replaces
        /// every occurrence of the first distinct wcid in Decor, style[1] the second, and so on. Empty, the
        /// default and every theme's value but sky_rift, means Decor spawns exactly as authored. When
        /// non-empty, each run picks ONE style uniformly at random when it spawns decor (WorldEventSpawner.
        /// PickDecorStyle) and substitutes it in (WorldEventSpawner.ApplyDecorStyle) before placement.
        /// Entries that fail validation are dropped individually at load; the theme itself is always kept -
        /// see WorldEventAxisStore.ValidateDecorStyles.
        /// </summary>
        [JsonPropertyName("decorStyles")]
        public List<List<uint>> DecorStyles { get; set; } = new List<List<uint>>();

        /// <summary>
        /// Spawn-time NPCs placed once around the geometry CENTRE when the run goes Active (WP-15). Empty,
        /// the default and every theme's value except weave_spiral, means the theme places no npc at all.
        /// Entries that fail validation are dropped individually at load; the theme itself is always kept.
        /// </summary>
        [JsonPropertyName("npcs")]
        public List<NpcDef> Npcs { get; set; } = new List<NpcDef>();

        /// <summary>
        /// WP-17 sky-drop: metres ABOVE TERRAIN at which WAVE creatures are placed. 0, the default and every
        /// shipped theme's value except sky_rift, means they are placed on the ground exactly as before.
        /// When it is greater than 0 the creature is placed that far up and falls under gravity to the
        /// ground before it can act - it is force-woken at spawn so it keeps ticking physics, and it holds
        /// every attack, move and target search until it lands.
        ///
        /// WAVE spawns ONLY. Sources (rifts), the champion, decor and npcs are unchanged whatever this says;
        /// a champion drop is a possible follow-up, not this spike. Validated to a finite value in
        /// [0, <see cref="MaxSpawnDz"/>] - anything else drops the whole theme with a diagnostic.
        /// </summary>
        [JsonPropertyName("spawnDz")]
        public float SpawnDz { get; set; }

        /// <summary>
        /// The ceiling on <see cref="SpawnDz"/>. The client stops animating objects far from the viewer, and
        /// no scene needs a longer fall than this.
        /// </summary>
        public const float MaxSpawnDz = 60f;

        /// <summary>
        /// Computed at load time from <see cref="Geometry"/> (case-insensitive). Never read from JSON.
        /// </summary>
        [JsonIgnore]
        public SourceGeometry GeometryKind { get; set; }
    }
}
