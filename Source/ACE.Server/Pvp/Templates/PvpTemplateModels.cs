using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.Entity.Facets;

namespace ACE.Server.Pvp.Templates
{
    // PvP Template Facets (Docs/Pvp/TEMPLATES.md): the two persisted shapes.
    //
    //   - PvpTemplateDefinition: the frozen build an admin snapshotted. Stored in pvp_template.definition_Json
    //     (mediumtext) and copied onto each participant's binding at dispatch, so a re-snapshot never changes a
    //     match in progress.
    //   - PvpTemplateRestoreRecord: everything the overlay overwrote on one player, stored in
    //     PropertyString 9022 PvpTemplateRestore (TEXT, capped at 60 KB by PvpTemplateJson). Its PRESENCE is
    //     the definition of "templated".
    //
    // JSON names are short and every dictionary is keyed by the raw enum INTEGER, never by the enum NAME, so
    // a future rename of an enum member cannot orphan a stored value (the same reason FacetSnapshot keys
    // abilities by a stable name rather than by a reorderable enum). Enum VALUES serialize as numbers
    // (System.Text.Json's default). Both shapes carry a schema version; see PvpTemplateJson.

    /// <summary>One primary attribute: the innate value and the XP-bought ranks.</summary>
    public class PvpTemplateAttributeEntry
    {
        [JsonPropertyName("il")] public uint InitLevel { get; set; }
        [JsonPropertyName("r")] public uint Ranks { get; set; }
        [JsonPropertyName("cp")] public uint CpSpent { get; set; }
    }

    /// <summary>One vital: ranks and CP spent, plus the current value (used by the restore record only).</summary>
    public class PvpTemplateVitalEntry
    {
        [JsonPropertyName("il")] public uint InitLevel { get; set; }
        [JsonPropertyName("r")] public uint Ranks { get; set; }
        [JsonPropertyName("cp")] public uint CpSpent { get; set; }
        [JsonPropertyName("cur")] public uint Current { get; set; }
    }

    /// <summary>
    /// One enchantment registry row, with short JSON names so a restore record holding a full buff bar stays
    /// well under its 60 KB cap. Field-for-field with PropertiesEnchantmentRegistry.
    /// </summary>
    public class PvpTemplateEnchantment
    {
        [JsonPropertyName("c")] public uint EnchantmentCategory { get; set; }
        [JsonPropertyName("s")] public int SpellId { get; set; }
        [JsonPropertyName("l")] public ushort LayerId { get; set; }
        [JsonPropertyName("hs")] public bool HasSpellSetId { get; set; }
        [JsonPropertyName("sc")] public uint SpellCategory { get; set; }
        [JsonPropertyName("p")] public uint PowerLevel { get; set; }
        [JsonPropertyName("st")] public double StartTime { get; set; }
        [JsonPropertyName("d")] public double Duration { get; set; }
        [JsonPropertyName("co")] public uint CasterObjectId { get; set; }
        [JsonPropertyName("dm")] public float DegradeModifier { get; set; }
        [JsonPropertyName("dl")] public float DegradeLimit { get; set; }
        [JsonPropertyName("ltd")] public double LastTimeDegraded { get; set; }
        [JsonPropertyName("t")] public uint StatModType { get; set; }
        [JsonPropertyName("k")] public uint StatModKey { get; set; }
        [JsonPropertyName("v")] public float StatModValue { get; set; }
        [JsonPropertyName("ss")] public uint SpellSetId { get; set; }

        public static PvpTemplateEnchantment From(PropertiesEnchantmentRegistry e) => new PvpTemplateEnchantment
        {
            EnchantmentCategory = e.EnchantmentCategory,
            SpellId = e.SpellId,
            LayerId = e.LayerId,
            HasSpellSetId = e.HasSpellSetId,
            SpellCategory = (uint)e.SpellCategory,
            PowerLevel = e.PowerLevel,
            StartTime = e.StartTime,
            Duration = e.Duration,
            CasterObjectId = e.CasterObjectId,
            DegradeModifier = e.DegradeModifier,
            DegradeLimit = e.DegradeLimit,
            LastTimeDegraded = e.LastTimeDegraded,
            StatModType = (uint)e.StatModType,
            StatModKey = e.StatModKey,
            StatModValue = e.StatModValue,
            SpellSetId = (uint)e.SpellSetId,
        };

        public PropertiesEnchantmentRegistry ToRegistry() => new PropertiesEnchantmentRegistry
        {
            EnchantmentCategory = EnchantmentCategory,
            SpellId = SpellId,
            LayerId = LayerId,
            HasSpellSetId = HasSpellSetId,
            SpellCategory = (SpellCategory)SpellCategory,
            PowerLevel = PowerLevel,
            StartTime = StartTime,
            Duration = Duration,
            CasterObjectId = CasterObjectId,
            DegradeModifier = DegradeModifier,
            DegradeLimit = DegradeLimit,
            LastTimeDegraded = LastTimeDegraded,
            StatModType = (EnchantmentTypeFlags)StatModType,
            StatModKey = StatModKey,
            StatModValue = StatModValue,
            SpellSetId = (EquipmentSet)SpellSetId,
        };
    }

    public class PvpTemplateAnimPart
    {
        [JsonPropertyName("i")] public byte Index { get; set; }
        [JsonPropertyName("a")] public uint AnimationId { get; set; }
    }

    public class PvpTemplatePalette
    {
        [JsonPropertyName("s")] public uint SubPaletteId { get; set; }
        [JsonPropertyName("o")] public ushort Offset { get; set; }
        [JsonPropertyName("l")] public ushort Length { get; set; }
    }

    public class PvpTemplateTextureMap
    {
        [JsonPropertyName("p")] public byte PartIndex { get; set; }
        [JsonPropertyName("o")] public uint OldTexture { get; set; }
        [JsonPropertyName("n")] public uint NewTexture { get; set; }
    }

    /// <summary>
    /// One item of the issued kit, captured from the template character's own possession. Holds the item's
    /// mutable property tables (so a tinkered, imbued or rolled item is reproduced exactly) but none of its
    /// identity or ownership: no guid, no instance ids (container, wielder, owner, allowed wielder), no
    /// positions. Emotes, create lists, generators and book pages are NOT carried; the live clone takes them
    /// from the item's weenie (see PvpTemplateKit).
    /// </summary>
    public class PvpTemplateKitItem
    {
        [JsonPropertyName("w")] public uint WeenieClassId { get; set; }
        [JsonPropertyName("wt")] public int WeenieType { get; set; }

        /// <summary>The EquipMask the template wore it in, as its raw integer. Null for a pack item.</summary>
        [JsonPropertyName("loc")] public int? WieldLocation { get; set; }

        [JsonPropertyName("i")] public Dictionary<int, int> Ints { get; set; } = new Dictionary<int, int>();
        [JsonPropertyName("i64")] public Dictionary<int, long> Int64s { get; set; } = new Dictionary<int, long>();
        [JsonPropertyName("b")] public Dictionary<int, bool> Bools { get; set; } = new Dictionary<int, bool>();
        [JsonPropertyName("f")] public Dictionary<int, double> Floats { get; set; } = new Dictionary<int, double>();
        [JsonPropertyName("s")] public Dictionary<int, string> Strings { get; set; } = new Dictionary<int, string>();
        [JsonPropertyName("did")] public Dictionary<int, uint> DataIds { get; set; } = new Dictionary<int, uint>();
        [JsonPropertyName("sb")] public Dictionary<int, float> SpellBook { get; set; } = new Dictionary<int, float>();
        [JsonPropertyName("ap")] public List<PvpTemplateAnimPart> AnimParts { get; set; } = new List<PvpTemplateAnimPart>();
        [JsonPropertyName("pal")] public List<PvpTemplatePalette> Palettes { get; set; } = new List<PvpTemplatePalette>();
        [JsonPropertyName("tm")] public List<PvpTemplateTextureMap> TextureMaps { get; set; } = new List<PvpTemplateTextureMap>();
        [JsonPropertyName("e")] public List<PvpTemplateEnchantment> Enchantments { get; set; } = new List<PvpTemplateEnchantment>();
    }

    /// <summary>
    /// A frozen template build (TEMPLATES.md "Templates"). Everything the overlay writes onto a player at match
    /// entry comes from here, and nothing else.
    /// </summary>
    public class PvpTemplateDefinition
    {
        [JsonPropertyName("key")] public string Key { get; set; }

        /// <summary>The pvp_template.version this definition was read at. Recorded on the participant row.</summary>
        [JsonPropertyName("ver")] public uint Version { get; set; }

        [JsonPropertyName("name")] public string DisplayName { get; set; }

        /// <summary>The six primary attributes, keyed by (int)PropertyAttribute.</summary>
        [JsonPropertyName("attr")] public Dictionary<int, PvpTemplateAttributeEntry> Attributes { get; set; } = new Dictionary<int, PvpTemplateAttributeEntry>();

        /// <summary>The three max vitals, keyed by (int)PropertyAttribute2nd. Current is ignored: entry is always full.</summary>
        [JsonPropertyName("vit")] public Dictionary<int, PvpTemplateVitalEntry> Vitals { get; set; } = new Dictionary<int, PvpTemplateVitalEntry>();

        /// <summary>The whole skill state. Applied authoritatively: a skill it does not name is Untrained on the player.</summary>
        [JsonPropertyName("sk")] public List<FacetSkillEntry> Skills { get; set; } = new List<FacetSkillEntry>();

        /// <summary>
        /// Values for <see cref="PvpTemplatePowerProperties.ReplacedInts"/>, keyed by (int)PropertyInt. A replaced
        /// property absent here is REMOVED from the player while templated (the template did not have it).
        /// </summary>
        [JsonPropertyName("int")] public Dictionary<int, int> PowerInts { get; set; } = new Dictionary<int, int>();

        /// <summary>The template buff set, normalized at snapshot (full duration, never an item or cooldown row).</summary>
        [JsonPropertyName("buff")] public List<PvpTemplateEnchantment> Buffs { get; set; } = new List<PvpTemplateEnchantment>();

        /// <summary>The template spellbook. Added to the player where missing; also the cast gate's allow list.</summary>
        [JsonPropertyName("sp")] public List<int> Spells { get; set; } = new List<int>();

        [JsonPropertyName("kit")] public List<PvpTemplateKitItem> Kit { get; set; } = new List<PvpTemplateKitItem>();
    }

    /// <summary>
    /// Everything one template apply overwrote, so the restore can put it back with ABSOLUTE values (a replay is
    /// a no-op). Written once, never over an existing record (TEMPLATES.md crash invariants 1 and 3).
    /// </summary>
    public class PvpTemplateRestoreRecord
    {
        [JsonPropertyName("match")] public Guid MatchId { get; set; }
        [JsonPropertyName("key")] public string TemplateKey { get; set; }
        [JsonPropertyName("ver")] public uint TemplateVersion { get; set; }
        [JsonPropertyName("at")] public DateTime AppliedAtUtc { get; set; }

        [JsonPropertyName("attr")] public Dictionary<int, PvpTemplateAttributeEntry> Attributes { get; set; } = new Dictionary<int, PvpTemplateAttributeEntry>();
        [JsonPropertyName("vit")] public Dictionary<int, PvpTemplateVitalEntry> Vitals { get; set; } = new Dictionary<int, PvpTemplateVitalEntry>();
        [JsonPropertyName("sk")] public List<FacetSkillEntry> Skills { get; set; } = new List<FacetSkillEntry>();

        /// <summary>The player's own value of every replaced PropertyInt; null means it was absent.</summary>
        [JsonPropertyName("int")] public Dictionary<int, int?> Ints { get; set; } = new Dictionary<int, int?>();

        /// <summary>Every non-item enchantment the player held at entry, removed by the apply.</summary>
        [JsonPropertyName("ench")] public List<PvpTemplateEnchantment> RemovedEnchantments { get; set; } = new List<PvpTemplateEnchantment>();

        /// <summary>
        /// Whether own buffs come back at exit (pvp_template_keep_own_buffs, decided at entry so a later setting
        /// change never alters a restore in flight). Vitae and cooldowns always come back.
        /// </summary>
        [JsonPropertyName("ownbuffs")] public bool ReturnOwnBuffs { get; set; } = true;

        /// <summary>Template spells the player did not already know: added at entry, removed at exit.</summary>
        [JsonPropertyName("addsp")] public List<int> AddedSpells { get; set; } = new List<int>();

        /// <summary>The template spellbook: the cast gate's allow list while templated (Phase B reads it).</summary>
        [JsonPropertyName("tsp")] public List<int> TemplateSpells { get; set; } = new List<int>();

        /// <summary>The player's own worn set at entry, re-equipped at exit through Player.RestoreFacetEquip.</summary>
        [JsonPropertyName("equip")] public List<FacetEquipEntry> OwnEquip { get; set; } = new List<FacetEquipEntry>();
    }
}
