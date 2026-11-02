using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ACE.Server.ThreadDungeons.Defs
{
    /// <summary>Root of Content/dungeons/dynamic/index.json.</summary>
    public class DungeonIndexDef
    {
        /// <summary>Schema version of this file. The store rejects anything it does not know (TECH-DESIGN S9.4).</summary>
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("dungeons")]
        public List<DungeonEntryDef> Dungeons { get; set; } = new List<DungeonEntryDef>();
    }

    /// <summary>
    /// One curated dungeon. The index row carries the curation fields; the per-landblock file
    /// (<0xLLLL>.json) carries Points, BossAnchor and Entry and is merged in by the store.
    /// </summary>
    public class DungeonEntryDef
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }

        /// <summary>Hex landblock id, e.g. "0x0150". Parsed into <see cref="Landblock"/> by the store.</summary>
        [JsonPropertyName("landblock")]
        public string LandblockHex { get; set; }

        [JsonIgnore]
        public ushort Landblock { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("minLevel")]
        public int MinLevel { get; set; }

        [JsonPropertyName("maxLevel")]
        public int MaxLevel { get; set; }

        /// <summary>Species-table ids (Content/events/axes/species) this dungeon prefers. Empty = any.</summary>
        [JsonPropertyName("families")]
        public List<string> Families { get; set; } = new List<string>();

        /// <summary>CreatureType names of the dungeon's original population, for the 3:1 family preference.</summary>
        [JsonPropertyName("creatureTypes")]
        public List<string> CreatureTypes { get; set; } = new List<string>();

        [JsonPropertyName("exitPortalWcid")]
        public uint ExitPortalWcid { get; set; }

        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = true;

        // ---- merged from <0xLLLL>.json ----

        [JsonPropertyName("entry")]
        public string Entry { get; set; }

        [JsonPropertyName("points")]
        public List<DungeonSpawnPointDef> Points { get; set; } = new List<DungeonSpawnPointDef>();

        [JsonPropertyName("bossAnchor")]
        public DungeonSpawnPointDef BossAnchor { get; set; }

        // ---- outdoor sites (Open World Threads) ----

        /// <summary>Index row kind: <see cref="KindDungeon"/> (the default when absent) or <see cref="KindOutdoor"/>.</summary>
        [JsonPropertyName("kind")]
        public string Kind { get; set; } = KindDungeon;

        public const string KindDungeon = "dungeon";
        public const string KindOutdoor = "outdoor";

        [JsonIgnore]
        public bool IsOutdoor => string.Equals(Kind, KindOutdoor, System.StringComparison.Ordinal);

        /// <summary>The parsed outdoor-site file (Content/dungeons/dynamic/outdoor/&lt;id&gt;.json) merged in by
        /// the store. Null for every dungeon-kind row.</summary>
        [JsonIgnore]
        public OutdoorSiteDef Outdoor { get; set; }
    }

    /// <summary>
    /// A circle on the outdoor grid. Landblock + local x,y name the centre exactly as a position does in the
    /// dungeon files; radius is metres. See Content/dungeons/dynamic/outdoor/README.md for the frame.
    /// </summary>
    public class OutdoorCircleDef
    {
        /// <summary>Hex landblock id of the centre, e.g. "0x5456".</summary>
        [JsonPropertyName("landblock")] public string LandblockHex { get; set; }
        [JsonIgnore] public ushort Landblock { get; set; }
        [JsonPropertyName("x")] public double X { get; set; }
        [JsonPropertyName("y")] public double Y { get; set; }
        [JsonPropertyName("radius")] public double Radius { get; set; }
    }

    /// <summary>One include/exclude area in GLOBAL outdoor metres: exactly one of Circle or Polygon is set.</summary>
    public class OutdoorAreaDef
    {
        [JsonPropertyName("circle")] public OutdoorAreaCircleDef Circle { get; set; }

        /// <summary>Vertices [[gx, gy], ...], at least three, implicitly closed.</summary>
        [JsonPropertyName("polygon")] public List<List<double>> Polygon { get; set; }
    }

    public class OutdoorAreaCircleDef
    {
        [JsonPropertyName("gx")] public double Gx { get; set; }
        [JsonPropertyName("gy")] public double Gy { get; set; }
        [JsonPropertyName("r")] public double R { get; set; }
    }

    /// <summary>Informational (the spawn tool reads it): one building whose interior cells hold "interior" points.</summary>
    public class OutdoorInteriorBuildingDef
    {
        [JsonPropertyName("landblock")] public string LandblockHex { get; set; }
        [JsonPropertyName("buildingIndex")] public int BuildingIndex { get; set; }
        [JsonPropertyName("envCells")] public List<uint> EnvCells { get; set; } = new List<uint>();
    }

    /// <summary>
    /// Root of Content/dungeons/dynamic/outdoor/&lt;id&gt;.json. Entry, BossAnchor and Points reuse the dungeon
    /// file shapes; the rest is outdoor-only. The store validates it and merges it into the index row's
    /// <see cref="DungeonEntryDef"/> (Entry, Points, BossAnchor, Outdoor).
    /// </summary>
    public class OutdoorSiteFileDef
    {
        [JsonPropertyName("version")] public int Version { get; set; } = 1;
        [JsonPropertyName("id")] public string Id { get; set; }
        [JsonPropertyName("primary")] public string PrimaryHex { get; set; }
        [JsonPropertyName("footprint")] public List<string> Footprint { get; set; } = new List<string>();
        [JsonPropertyName("circle")] public OutdoorCircleDef Circle { get; set; }
        [JsonPropertyName("entry")] public string Entry { get; set; }
        [JsonPropertyName("exit")] public string Exit { get; set; }
        [JsonPropertyName("bossAnchor")] public DungeonSpawnPointDef BossAnchor { get; set; }
        [JsonPropertyName("include")] public List<OutdoorAreaDef> Include { get; set; } = new List<OutdoorAreaDef>();
        [JsonPropertyName("exclude")] public List<OutdoorAreaDef> Exclude { get; set; } = new List<OutdoorAreaDef>();
        [JsonPropertyName("interiorBuildings")] public List<OutdoorInteriorBuildingDef> InteriorBuildings { get; set; } = new List<OutdoorInteriorBuildingDef>();
        [JsonPropertyName("targetPoints")] public int TargetPoints { get; set; }
        [JsonPropertyName("seed")] public int Seed { get; set; }
        [JsonPropertyName("points")] public List<DungeonSpawnPointDef> Points { get; set; } = new List<DungeonSpawnPointDef>();
    }

    /// <summary>The validated outdoor-only part of a site, hung off <see cref="DungeonEntryDef.Outdoor"/>.</summary>
    public class OutdoorSiteDef
    {
        public ushort Primary { get; set; }
        public List<ushort> Footprint { get; set; } = new List<ushort>();
        public OutdoorCircleDef Circle { get; set; }

        /// <summary>The exit-portal position, same text shape as <see cref="DungeonEntryDef.Entry"/>.</summary>
        public string Exit { get; set; }
        public List<OutdoorAreaDef> Include { get; set; } = new List<OutdoorAreaDef>();
        public List<OutdoorAreaDef> Exclude { get; set; } = new List<OutdoorAreaDef>();
        public List<OutdoorInteriorBuildingDef> InteriorBuildings { get; set; } = new List<OutdoorInteriorBuildingDef>();
        public int TargetPoints { get; set; }
        public int Seed { get; set; }
    }

    /// <summary>Root of Content/dungeons/dynamic/<0xLLLL>.json, written by the dungeonspawns tool.</summary>
    public class DungeonSpawnFileDef
    {
        /// <summary>Schema version of this file. The store rejects anything it does not know (TECH-DESIGN S9.4).</summary>
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("landblock")]
        public string LandblockHex { get; set; }

        [JsonPropertyName("entry")]
        public string Entry { get; set; }

        [JsonPropertyName("points")]
        public List<DungeonSpawnPointDef> Points { get; set; } = new List<DungeonSpawnPointDef>();

        [JsonPropertyName("bossAnchor")]
        public DungeonSpawnPointDef BossAnchor { get; set; }
    }

    public class DungeonSpawnPointDef
    {
        [JsonPropertyName("cell")]
        public uint Cell { get; set; }

        [JsonPropertyName("x")] public float X { get; set; }
        [JsonPropertyName("y")] public float Y { get; set; }
        [JsonPropertyName("z")] public float Z { get; set; }
        [JsonPropertyName("qw")] public float QW { get; set; } = 1f;
        [JsonPropertyName("qx")] public float QX { get; set; }
        [JsonPropertyName("qy")] public float QY { get; set; }
        [JsonPropertyName("qz")] public float QZ { get; set; }

        /// <summary>Metres of free floor around the point, from the tool's clearance probe.</summary>
        [JsonPropertyName("clearance")]
        public float Clearance { get; set; }

        /// <summary>BFS depth from the entry cell; the boss anchor is the deepest curated point.</summary>
        [JsonPropertyName("depth")]
        public int Depth { get; set; }

        /// <summary>False = extracted but rejected by curation; the spawner skips it.</summary>
        [JsonPropertyName("curated")]
        public bool Curated { get; set; } = true;

        /// <summary>Original generator guid, informational.</summary>
        [JsonPropertyName("source")]
        public string Source { get; set; }

        /// <summary>Outdoor sites only: "outdoor" (open ground) or "interior" (inside a building). Null in dungeon files.</summary>
        [JsonPropertyName("kind")]
        public string Kind { get; set; }

        /// <summary>Outdoor sites only, optional: tallest collision height in metres that fits this point
        /// (interior points under low doorways). Null = no constraint recorded.</summary>
        [JsonPropertyName("maxCollisionHeight")]
        public float? MaxCollisionHeight { get; set; }

        /// <summary>
        /// Runtime only, never in a file: true for an overflow copy of an outdoor point made by
        /// OutdoorPopulation.ScatterCopy. Its z is the source point's and is snapped to the terrain at placement.
        /// </summary>
        [JsonIgnore]
        public bool Scattered { get; set; }
    }

    public class BossListDef
    {
        /// <summary>Schema version of this file. The store rejects anything it does not know (TECH-DESIGN S9.4).</summary>
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("bosses")]
        public List<BossEntryDef> Bosses { get; set; } = new List<BossEntryDef>();
    }

    public class BossEntryDef
    {
        [JsonPropertyName("wcid")]
        public uint Wcid { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("level")]
        public int Level { get; set; }

        /// <summary>Species-table ids this boss may headline. Empty = any.</summary>
        [JsonPropertyName("families")]
        public List<string> Families { get; set; } = new List<string>();

        /// <summary>Modifier ids (target "boss") always applied to this boss.</summary>
        [JsonPropertyName("modifiers")]
        public List<string> Modifiers { get; set; } = new List<string>();
    }

    public class ModifierListDef
    {
        /// <summary>Schema version of this file. The store rejects anything it does not know (TECH-DESIGN S9.4).</summary>
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("modifiers")]
        public List<ModifierDef> Modifiers { get; set; } = new List<ModifierDef>();

        [JsonPropertyName("xpLadder")]
        public List<XpLadderRungDef> XpLadder { get; set; } = new List<XpLadderRungDef>();
    }

    /// <summary>
    /// One gem modifier. MonsterEffectKind names the creature knob the magnitude is written to:
    /// "health_mult", "damage_rating", "crit_rating", "crit_damage_rating", "damage_resist_rating",
    /// "count_mult", "elite_share", "run_speed_mult", "salvage_affinity", or "none". RewardXpKind is
    /// "constant" (base only) or "linear" (base + slope * magnitude). Luminance uses the same shape via
    /// RewardLumBase/Slope.
    ///
    /// "salvage_affinity" is the one kind whose magnitude is not written to a creature knob at all: it is a
    /// PER-KILL percent chance, in [0, 100], that the corpse carries one extra item of
    /// <see cref="SalvageMaterial"/> for the player to salvage. Per kill rather than per rolled item, so the
    /// printed number is the observed rate and stays orthogonal to lootQuantityMult.
    /// </summary>
    public class ModifierDef
    {
        [JsonPropertyName("id")] public string Id { get; set; }
        [JsonPropertyName("display")] public string Display { get; set; }
        [JsonPropertyName("rarity")] public string Rarity { get; set; } = "common";
        [JsonPropertyName("target")] public string Target { get; set; } = "monster";
        [JsonPropertyName("minMagnitude")] public double MinMagnitude { get; set; }
        [JsonPropertyName("maxMagnitude")] public double MaxMagnitude { get; set; }
        [JsonPropertyName("monsterEffectKind")] public string MonsterEffectKind { get; set; } = "none";
        [JsonPropertyName("rewardXpKind")] public string RewardXpKind { get; set; } = "constant";
        [JsonPropertyName("rewardXpBase")] public double RewardXpBase { get; set; } = 1.0;
        [JsonPropertyName("rewardXpSlope")] public double RewardXpSlope { get; set; }
        [JsonPropertyName("rewardLumBase")] public double RewardLumBase { get; set; } = 1.0;
        [JsonPropertyName("rewardLumSlope")] public double RewardLumSlope { get; set; }
        [JsonPropertyName("lootQualityBonus")] public double LootQualityBonus { get; set; }

        /// <summary>
        /// RELATIVE LIKELIHOOD OF BEING DRAWN BY A RANDOM SELECTION. 1.0 (the default, and what an absent
        /// field means) is "as likely as anything else in the pool"; 0.5 is half as likely; 0.0 is "never
        /// drawn at random, only reachable by loading the component that names it". Values above 1.0 are
        /// legal and are how a row is made commoner than the field.
        ///
        /// It scales the FOUR random paths that can add a modifier to a gem - the empty-slot component draw,
        /// the Turquoise Taper's add_random, an aim MISS, and reroll_one - and nothing else. It never affects
        /// a component the player deliberately loaded: a White Taper still aims at hollow at the ordinary
        /// <see cref="PressLimits.AimChance"/>, and a named op still lands on its hit.
        ///
        /// Deliberately NOT keyed on <see cref="Rarity"/>. Rarity is /dd give's knob, and three other rows
        /// share hollow's "rare" tag; this one is per modifier so the catalog can be tuned a row at a time
        /// once there is play data. Today hollow is the only shipped row below 1.0.
        /// </summary>
        [JsonPropertyName("drawWeight")] public double DrawWeight { get; set; } = 1.0;

        /// <summary>
        /// Per-modifier factor on how MANY items a kill drops. Multiplied across the gem's modifiers and
        /// clamped by dynamic_dungeons_loot_quantity_cap; 1.0 (the default) is a no-op, so a row that omits
        /// the field changes nothing.
        /// </summary>
        [JsonPropertyName("lootQuantityMult")] public double LootQuantityMult { get; set; } = 1.0;

        /// <summary>
        /// For a "salvage_affinity" row: the ACE.Entity.Enum.MaterialType the injected item is made of.
        /// Validated at load - a value that is not a defined MaterialType drops the row with a diagnostic.
        /// Meaningless on any other monsterEffectKind.
        /// </summary>
        [JsonPropertyName("salvageMaterial")] public int SalvageMaterial { get; set; }

        /// <summary>
        /// For a "salvage_affinity" row: the ordinary weenie the injected item is made FROM. Carried on the
        /// row alongside the material so a fourth material is one JSON row and nothing has to infer whether a
        /// material is a gem or a stone - Obsidian is a Stone-group material with no gem, so its base item is
        /// dinnerware while Tourmaline's and Amethyst's are gems. All three shipped bases have Value 100, and
        /// that parity matters: Player_Crafting.AddSalvage folds item.Value into the resulting bag's Value, so
        /// a higher-value base would make one material's bags worth more for identical effort.
        ///
        /// Validated in two halves. The non-zero check is at parse time; the "resolves to a real weenie"
        /// check has to wait for the world database and lives in
        /// <see cref="ThreadDungeonStore.ValidateWorldWcids"/>.
        /// </summary>
        [JsonPropertyName("salvageBaseWcid")] public uint SalvageBaseWcid { get; set; }
    }

    public class XpLadderRungDef
    {
        [JsonPropertyName("level")] public int Level { get; set; }
        [JsonPropertyName("xp")] public long Xp { get; set; }
    }

    /// <summary>
    /// Root of Content/dungeons/dynamic/clearance.json - the measured physical clearance of every curated
    /// dungeon, written by tools/dungeon-clearance/build-clearance-table.ps1 out of
    /// ACE.Content.Tools "cells --clearance --json".
    ///
    /// Read by <see cref="ACE.Server.ThreadDungeons.DungeonFitFilter"/> to decide which roster creatures
    /// physically fit a given dungeon. NO CLEARANCE ROW MEANS NO CONSTRAINT, never "nothing fits" - see
    /// <see cref="ACE.Server.ThreadDungeons.ThreadDungeonStore.Clearance"/> for the whole fail-open contract.
    /// </summary>
    public class DungeonClearanceFileDef
    {
        /// <summary>Schema version of this file. The store rejects anything it does not know (TECH-DESIGN S9.4).</summary>
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("dungeons")]
        public List<DungeonClearanceEntryDef> Dungeons { get; set; } = new List<DungeonClearanceEntryDef>();
    }

    /// <summary>
    /// One dungeon's measured clearance. Keyed by <see cref="Id"/>, which must match an index.json dungeon
    /// id; <see cref="LandblockHex"/> is carried for cross-checking against that same index row and is not
    /// itself the key.
    /// </summary>
    public class DungeonClearanceEntryDef
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }

        /// <summary>Hex landblock id, e.g. "0x0150". Cross-checked against the index entry's, never parsed into a key.</summary>
        [JsonPropertyName("landblock")]
        public string LandblockHex { get; set; }

        /// <summary>Mirrors the index row's own enabled flag. Carried for the audit trail only - whether a
        /// dungeon can be drawn at all is decided by index.json, not here.</summary>
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Metres: the tallest body that still reaches EVERY reference-reachable cell of this dungeon, at any
        /// width. This is the one field the fit predicate reads today.
        ///
        /// A value at or below zero means "not measured", which reads as NO CONSTRAINT (every creature fits),
        /// exactly as an absent row does.
        /// </summary>
        [JsonPropertyName("maxCollisionHeightFullAccess")]
        public double MaxCollisionHeightFullAccess { get; set; }

        /// <summary>
        /// The width-at-height curve. NOTHING READS THIS YET, and it is carried deliberately: adding the
        /// second (radius) axis to the fit predicate later is then a predicate change and not a schema
        /// change. Measured over 1040 creature-dungeon pairs, radius never bound - height alone predicted
        /// pass/fail with zero disagreements - so the shipped predicate is height-only.
        /// </summary>
        [JsonPropertyName("envelope")]
        public List<DungeonClearanceEnvelopeDef> Envelope { get; set; } = new List<DungeonClearanceEnvelopeDef>();
    }

    /// <summary>
    /// One rung of a dungeon's width-at-height curve. Every radius here is a COLLISION SPHERE radius, never
    /// Setup.Radius (which is label and melee-reach data and runs about 40 percent higher).
    /// </summary>
    public class DungeonClearanceEnvelopeDef
    {
        /// <summary>Metres of body height this rung was measured at.</summary>
        [JsonPropertyName("collisionHeight")]
        public double CollisionHeight { get; set; }

        /// <summary>Widest collision radius that fits the tightest portal of the route at this height.</summary>
        [JsonPropertyName("maxCollisionRadius")]
        public double MaxCollisionRadius { get; set; }

        /// <summary>Radius still reaching 99 percent of the reference-reachable floor area at this height.</summary>
        [JsonPropertyName("collisionRadius99")]
        public double CollisionRadius99 { get; set; }

        /// <summary>Radius still reaching 95 percent of the reference-reachable floor area at this height.</summary>
        [JsonPropertyName("collisionRadius95")]
        public double CollisionRadius95 { get; set; }

        /// <summary>Radius still reaching 90 percent of the reference-reachable floor area at this height.</summary>
        [JsonPropertyName("collisionRadius90")]
        public double CollisionRadius90 { get; set; }
    }

    /// <summary>
    /// Root of Content/dungeons/dynamic/weak-points.json - the attack height each species-table family's
    /// creatures are weak at (see <see cref="ACE.Server.ThreadDungeons.DungeonWeakPoint"/>). A family with no
    /// row gets no weak point and a once-per-run WARN, never a refused run.
    /// </summary>
    public class WeakPointFileDef
    {
        /// <summary>Schema version of this file. The store rejects anything it does not know (TECH-DESIGN S9.4).</summary>
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("families")]
        public List<WeakPointFamilyDef> Families { get; set; } = new List<WeakPointFamilyDef>();
    }

    /// <summary>
    /// One family's weak-point height. <see cref="Height"/> and <see cref="BossHeight"/> are
    /// high|medium|low|random; <c>random</c> means the family has no height differential and its creatures
    /// cut a third of their hittable parts instead. An absent <see cref="BossHeight"/> means the boss uses
    /// <see cref="Height"/>.
    /// </summary>
    public class WeakPointFamilyDef
    {
        [JsonPropertyName("family")]
        public string Family { get; set; }

        [JsonPropertyName("height")]
        public string Height { get; set; }

        [JsonPropertyName("bossHeight")]
        public string BossHeight { get; set; }
    }
}
