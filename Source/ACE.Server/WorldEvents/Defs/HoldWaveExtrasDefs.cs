using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ACE.Server.WorldEvents.Defs
{
    /// <summary>
    /// protect.ward: the ward on a mixed wave's ranged monsters (Hold runs only). While it is up, the damage kinds
    /// in <see cref="Reduces"/> are cut by <see cref="Reduction"/>. A hit of a kind in <see cref="BreakOn"/>
    /// shatters it for <see cref="ShatterSeconds"/>, when everything hits normally; then it comes back. Each change
    /// plays an effect on the monster. Player-facing text is ASCII only.
    /// </summary>
    public class WardDef
    {
        /// <summary>Share of damage taken off while the ward is up (0 to 1). Default 0.75.</summary>
        [JsonPropertyName("reduction")]
        public double Reduction { get; set; } = 0.75;

        /// <summary>Seconds the ward stays down after it shatters (1 to 120). Default 10.</summary>
        [JsonPropertyName("shatterSeconds")]
        public double ShatterSeconds { get; set; } = 10;

        /// <summary>Damage kinds the ward reduces: melee, missile, magic. Default missile and magic.</summary>
        [JsonPropertyName("reduces")]
        public List<string> Reduces { get; set; } = new List<string> { "missile", "magic" };

        /// <summary>Damage kinds that shatter the ward: melee, missile, magic. Default melee.</summary>
        [JsonPropertyName("breakOn")]
        public List<string> BreakOn { get; set; } = new List<string> { "melee" };

        /// <summary>The effect when the ward shatters (a PlayScript name, or none). Default ShieldDownBlue.</summary>
        [JsonPropertyName("breakEffect")]
        public string BreakEffect { get; set; } = "ShieldDownBlue";

        /// <summary>The effect when the ward comes back, and when a warded monster arrives. Default ShieldUpBlue.</summary>
        [JsonPropertyName("restoreEffect")]
        public string RestoreEffect { get; set; } = "ShieldUpBlue";

        /// <summary>A World Event line after the wave's own, one picked at random, when the wave has warded ranged.</summary>
        [JsonPropertyName("announce")]
        public List<string> Announce { get; set; } = new List<string>();

        /// <summary>Parsed from <see cref="Reduces"/> at load. Never read from JSON.</summary>
        [JsonIgnore]
        public WorldEventCombatKinds ReducesKinds { get; set; } = WorldEventCombatKinds.Missile | WorldEventCombatKinds.Magic;

        /// <summary>Parsed from <see cref="BreakOn"/> at load. Never read from JSON.</summary>
        [JsonIgnore]
        public WorldEventCombatKinds BreakOnKinds { get; set; } = WorldEventCombatKinds.Melee;
    }

    /// <summary>The kinds of damage a Hold ward tells apart.</summary>
    [System.Flags]
    public enum WorldEventCombatKinds
    {
        None = 0,
        Melee = 1,
        Missile = 2,
        Magic = 4
    }

    /// <summary>
    /// protect.corrupted: the mixed wave's corrupted monsters. Marked (title, size, a repeating effect), tougher,
    /// and while one lives the wave monsters within <see cref="EmpowerRadius"/> of it hit the targets
    /// <see cref="EmpowerMult"/> times as hard; the corrupted one itself hits them <see cref="SelfMult"/> times as hard.
    /// </summary>
    public class CorruptedDef
    {
        /// <summary>The title put in front of its name (letters and spaces, up to 20). Default Corrupted.</summary>
        [JsonPropertyName("prefix")]
        public string Prefix { get; set; } = "Corrupted";

        /// <summary>Size multiplier (1 to 2). Default 1.25.</summary>
        [JsonPropertyName("scale")]
        public double Scale { get; set; } = 1.25;

        /// <summary>Health multiplier (1 to 5). Default 1.5.</summary>
        [JsonPropertyName("healthMult")]
        public double HealthMultJson { get; set; } = 1.5;

        /// <summary>In force: its Hold setting (HoldKeys.CorruptedHealthMult) when set, else goals.json (live).</summary>
        [JsonIgnore]
        public double HealthMult { get => HoldSettings.Number(HoldKeys.CorruptedHealthMult, HealthMultJson); set => HealthMultJson = value; }

        /// <summary>The effect replayed on it (a PlayScript name, or none). Default ShieldUpPurple.</summary>
        [JsonPropertyName("effect")]
        public string Effect { get; set; } = "ShieldUpPurple";

        /// <summary>How often the effect replays, in seconds (1 to 60). Default 3.</summary>
        [JsonPropertyName("effectSeconds")]
        public double EffectSeconds { get; set; } = 3;

        /// <summary>A second effect played with the first, for a stronger mark (a PlayScript name, or none). Default none.</summary>
        [JsonPropertyName("extraEffect")]
        public string ExtraEffect { get; set; } = "none";

        /// <summary>The radius in metres of its empowering aura (0 to 50). Default 12.</summary>
        [JsonPropertyName("empowerRadius")]
        public double EmpowerRadius { get; set; } = 12;

        /// <summary>How much harder wave monsters inside the aura hit the targets (1 to 5). Default 1.5.</summary>
        [JsonPropertyName("empowerMult")]
        public double EmpowerMultJson { get; set; } = 1.5;

        /// <summary>In force: its Hold setting (HoldKeys.CorruptedEmpowerMult) when set, else goals.json (live).</summary>
        [JsonIgnore]
        public double EmpowerMult { get => HoldSettings.Number(HoldKeys.CorruptedEmpowerMult, EmpowerMultJson); set => EmpowerMultJson = value; }

        /// <summary>How much harder the corrupted monster itself hits the targets (1 to 10). Default 2. Its hits count against protect.minDeathSeconds like every other run monster's.</summary>
        [JsonPropertyName("selfMult")]
        public double SelfMultJson { get; set; } = 2.0;

        /// <summary>In force: its Hold setting (HoldKeys.CorruptedSelfMult) when set, else goals.json (live).</summary>
        [JsonIgnore]
        public double SelfMult { get => HoldSettings.Number(HoldKeys.CorruptedSelfMult, SelfMultJson); set => SelfMultJson = value; }

        /// <summary>
        /// The corrupted assault: every this many waves (from <see cref="AssaultFromWave"/>), never the finale, the wave
        /// brings <see cref="AssaultCount"/> extra corrupted monsters, each sent at one named target and nothing else.
        /// The other waves bring none. 0 (the default) = off: corrupted come in every mixed wave by waveMix.corrupted.
        /// </summary>
        [JsonPropertyName("assaultEveryWaves")]
        public int AssaultEveryWavesJson { get; set; }

        /// <summary>In force: its Hold setting (HoldKeys.CorruptedAssaultEveryWaves) when set, else goals.json (live).</summary>
        [JsonIgnore]
        public int AssaultEveryWaves { get => HoldSettings.Integer(HoldKeys.CorruptedAssaultEveryWaves, AssaultEveryWavesJson); set => AssaultEveryWavesJson = value; }

        /// <summary>The first assault wave (1 to 10). Default 2.</summary>
        [JsonPropertyName("assaultFromWave")]
        public int AssaultFromWaveJson { get; set; } = 2;

        /// <summary>In force: its Hold setting (HoldKeys.CorruptedAssaultFromWave) when set, else goals.json (live).</summary>
        [JsonIgnore]
        public int AssaultFromWave { get => HoldSettings.Integer(HoldKeys.CorruptedAssaultFromWave, AssaultFromWaveJson); set => AssaultFromWaveJson = value; }

        /// <summary>Corrupted monsters per assault (1 to 5), each on a different target while enough stand. Default 2.</summary>
        [JsonPropertyName("assaultCount")]
        public int AssaultCountJson { get; set; } = 2;

        /// <summary>In force: its Hold setting (HoldKeys.CorruptedAssaultCount) when set, else goals.json (live).</summary>
        [JsonIgnore]
        public int AssaultCount { get => HoldSettings.Integer(HoldKeys.CorruptedAssaultCount, AssaultCountJson); set => AssaultCountJson = value; }

        /// <summary>
        /// One World Event line per assault monster, one picked at random: tokens {monster} (its name, "Corrupted Grievver
        /// Shredder"), {target}, {family}, {Family}. Without lines: "A {monster} marches on {target}!".
        /// </summary>
        [JsonPropertyName("assaultAnnounce")]
        public List<string> AssaultAnnounce { get; set; } = new List<string>();

        /// <summary>True when the corrupted come as periodic assaults (<see cref="AssaultEveryWaves"/> above 0).</summary>
        [JsonIgnore]
        public bool AssaultMode => AssaultEveryWaves > 0;

        /// <summary>A World Event line, one picked at random, when a wave brings corrupted monsters.</summary>
        [JsonPropertyName("announce")]
        public List<string> Announce { get; set; } = new List<string>();

        /// <summary>
        /// The tint: a client palette (0x04xxxxxx) laid over the corrupted monster's whole palette (all 2048 colors), so
        /// its body takes that color while keeping its shading. Written as "0x04001381" or a number; 0 or "none" = no
        /// tint. Shows on every model whose textures use a palette (most creatures); a model with only true-color
        /// textures keeps its look and relies on the aura (logged at spawn). Default none; the envoys use 0x04001381.
        /// </summary>
        [JsonPropertyName("tintPalette")]
        public string TintPaletteJson { get; set; } = "none";

        /// <summary>In force: world_events_hold_corrupted_tint when set ("none" = no color), else goals.json (next spawn).</summary>
        [JsonIgnore]
        public string TintPalette { get => HoldSettings.Text(HoldKeys.CorruptedTint, TintPaletteJson); set => TintPaletteJson = value; }

        /// <summary>The parsed goals.json <see cref="TintPalette"/> (0 = none). Set at load.</summary>
        [JsonIgnore]
        public uint TintPaletteIdJson { get; set; }

        /// <summary>The palette in force (0 = none): the setting's when set and valid, else goals.json's.</summary>
        [JsonIgnore]
        public uint TintPaletteId
        {
            get
            {
                if (!HoldSettings.IsSet(HoldKeys.CorruptedTint))
                    return TintPaletteIdJson;

                return ACE.Server.WorldEvents.WorldEventTint.TryParsePalette(TintPalette, out var palette) ? palette : TintPaletteIdJson;
            }
            set => TintPaletteIdJson = value;
        }
    }

    /// <summary>
    /// protect.shrine: a restoration shrine placed beside each target. A player uses it (from up to
    /// <see cref="UseRadius"/> away) to restore that target by protect.shrineHeal, then it rests for
    /// protect.shrineCooldownSeconds. Placed stuck, removed at cleanup with the rest of the scene.
    /// </summary>
    public class ShrineDef
    {
        /// <summary>
        /// The weenie. Default 2000008, the DreamWeave Restoration Shrine: a plain Generic that copies only the retail Altar
        /// model (1 m, no particles), with no PK/NPK switch, emotes or use actions. It must be a Generic (the spawner refuses
        /// anything else): a retail altar keeps its own use behavior in the client (the Altar of Asheron, 855, showed the
        /// NPK prompt), and the Essence Font (19484) is a 6 m constant blue flame.
        /// </summary>
        [JsonPropertyName("wcid")]
        public uint Wcid { get; set; } = 2000008;

        /// <summary>The name players see (letters and spaces, up to 30). Default Restoration Shrine.</summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = "Restoration Shrine";

        /// <summary>Size (0.1 to 3). Default 1.</summary>
        [JsonPropertyName("scale")]
        public double Scale { get; set; } = 1.0;

        /// <summary>Where it stands in the target's own frame: metres to its right. Default 3.</summary>
        [JsonPropertyName("dx")]
        public double Dx { get; set; } = 3.0;

        /// <summary>Where it stands in the target's own frame: metres outward (negative is toward the anchor). Default -1.5.</summary>
        [JsonPropertyName("dy")]
        public double Dy { get; set; } = -1.5;

        /// <summary>How far away a player may use it, in metres (1 to 10). Default 4.</summary>
        [JsonPropertyName("useRadius")]
        public double UseRadiusJson { get; set; } = 4;

        /// <summary>In force: world_events_hold_shrine_use_radius when set, else goals.json (applied as shrines are placed).</summary>
        [JsonIgnore]
        public double UseRadius { get => HoldSettings.Number(HoldKeys.ShrineUseRadius, UseRadiusJson); set => UseRadiusJson = value; }

        /// <summary>The effect on the shrine when it is ready again (a PlayScript name, or none): a small brief cue, never a constant one. Default SkillUpGreen.</summary>
        [JsonPropertyName("readyEffect")]
        public string ReadyEffect { get; set; } = "SkillUpGreen";

        /// <summary>
        /// How often a ready shrine replays its ready effect, in seconds (0 = only once, when it becomes ready; else 5 to
        /// 120). Default 0.
        /// </summary>
        [JsonPropertyName("readyPulseSeconds")]
        public double ReadyPulseSeconds { get; set; }

        /// <summary>The effect on the target when the shrine restores it (a PlayScript name, or none). Default HealthUpRed.</summary>
        [JsonPropertyName("usedEffect")]
        public string UsedEffect { get; set; } = "HealthUpRed";
    }

    /// <summary>
    /// protect.portals: small portals placed evenly around the outside of the spawn ring when the run starts. Every
    /// wave spawns at them (round-robin), and they flash when a wave comes through. Decor only: placed stuck,
    /// never usable, removed at cleanup.
    /// </summary>
    public class PortalsDef
    {
        /// <summary>How many portals (1 to 8). Default 4.</summary>
        [JsonPropertyName("count")]
        public int Count { get; set; } = 4;

        /// <summary>Their distance from the anchor in metres (5 to 80). Default 30.</summary>
        [JsonPropertyName("radius")]
        public double Radius { get; set; } = 30;

        /// <summary>Compass bearing of the first portal (0 = north, clockwise). Default 45, between the targets.</summary>
        [JsonPropertyName("rotationDegrees")]
        public double RotationDegrees { get; set; } = 45;

        /// <summary>Size of each portal against <see cref="Parts"/> as authored (0.1 to 2). Default 0.35.</summary>
        [JsonPropertyName("scale")]
        public double Scale { get; set; } = 0.35;

        /// <summary>How far inside each portal (toward the anchor) monsters appear, in metres (0 to 10). Default 2.</summary>
        [JsonPropertyName("inset")]
        public double Inset { get; set; } = 2;

        /// <summary>
        /// One portal's decor, in the portal's own frame (dx right, dy outward, yaw relative), as authored at full
        /// size; dx, dy, dz and scale are multiplied by <see cref="Scale"/>.
        /// </summary>
        [JsonPropertyName("parts")]
        public List<DecorDef> Parts { get; set; } = new List<DecorDef>();

        /// <summary>The effect played on every portal part when a wave spawns (a PlayScript name, or none). Default PortalExit.</summary>
        [JsonPropertyName("pulseEffect")]
        public string PulseEffect { get; set; } = "PortalExit";
    }

    /// <summary>
    /// protect.clearArea: the spawn-area check (WorldEventSpawnArea). Before a Hold run places its scene, the knight ring
    /// and each portal's path in are checked against the landscape: buildings and their cells, trees, rocks and other
    /// static or scenery objects, placed objects (NPCs, signs), water and slopes too steep to walk. A blocked portal is
    /// moved along the ring or in and out; one that cannot be moved is skipped. When the knights cannot stand at the
    /// anchor, or fewer than minPortals portals can be placed, the nearest open spot within searchRadius is used; with
    /// none, the start is refused (or the chain looks elsewhere).
    /// </summary>
    public class ClearAreaDef
    {
        /// <summary>Clear ground needed around each knight (and its shrine and pennant), in metres (1 to 10). Default 4.</summary>
        [JsonPropertyName("targetClearance")]
        public double TargetClearance { get; set; } = 4;

        /// <summary>Clear ground needed around each portal, in metres (1 to 10). Default 3.</summary>
        [JsonPropertyName("portalClearance")]
        public double PortalClearance { get; set; } = 3;

        /// <summary>Clear width either side of a portal's path to the knights, in metres (0.5 to 5). Default 1.5.</summary>
        [JsonPropertyName("pathClearance")]
        public double PathClearance { get; set; } = 1.5;

        /// <summary>How far a blocked portal may be turned along its ring, in degrees either way (0 to 60). Default 30.</summary>
        [JsonPropertyName("maxPortalShiftDegrees")]
        public double MaxPortalShiftDegrees { get; set; } = 30;

        /// <summary>How far a blocked portal may be moved in or out, in metres (0 to 15). Default 8.</summary>
        [JsonPropertyName("maxPortalShiftMetres")]
        public double MaxPortalShiftMetres { get; set; } = 8;

        /// <summary>The fewest portals a run may keep (1 to 8). Default 2.</summary>
        [JsonPropertyName("minPortals")]
        public int MinPortals { get; set; } = 2;

        /// <summary>How far from the asked spot an open one is looked for, in metres (0 to 100; 0 = refuse at once). Default 40.</summary>
        [JsonPropertyName("searchRadius")]
        public double SearchRadius { get; set; } = 40;

        /// <summary>Objects smaller than this radius are ignored (grass, flowers, small plants), in metres (0 to 2). Default 0.4.</summary>
        [JsonPropertyName("ignoreRadius")]
        public double IgnoreRadius { get; set; } = 0.4;
    }
}
