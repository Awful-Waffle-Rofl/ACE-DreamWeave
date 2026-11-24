using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ACE.Server.WorldEvents.Defs
{
    /// <summary>
    /// One member row of a species reference table (TECH-DESIGN C15, 2026-08-16). Purely informational -
    /// the engine NEVER reads Name/Level/Custom/Note from this file, only Wcid and Role; the live weenie
    /// is the source of truth for everything else (WorldEventCatalogBuilder).
    /// </summary>
    public class SpeciesMemberDef
    {
        [JsonPropertyName("wcid")]
        public uint Wcid { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("level")]
        public int Level { get; set; }

        [JsonPropertyName("role")]
        public int Role { get; set; }

        [JsonPropertyName("custom")]
        public bool Custom { get; set; }

        [JsonPropertyName("note")]
        public string Note { get; set; }
    }

    /// <summary>
    /// One entry from a `Content/events/axes/species/*.json` file (TECH-DESIGN C15, 2026-08-16) - a
    /// per-species reference table that ADDS to the weenie-scan family catalog (WorldEventCatalogBuilder),
    /// rather than replacing it. Species tables MAY reference retail creatures - C15 relaxes the PLAN 1.5
    /// "no retail creatures are ever drawn" line - leaning toward custom mobs but allowed to draw retail
    /// until the custom bench is deeper.
    ///
    /// The file stem must equal <see cref="Id"/>; a valid table also registers a <see cref="FamilyDef"/> in
    /// the axis store's Families (WorldEventAxisStore), so a species-only family resolves in the composer
    /// exactly like a families.json-declared one.
    /// </summary>
    public class SpeciesTableDef
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("displayName")]
        public string DisplayName { get; set; }

        [JsonPropertyName("hueKey")]
        public string HueKey { get; set; }

        [JsonPropertyName("biomeTags")]
        public List<string> BiomeTags { get; set; } = new List<string>();

        /// <summary>Informational only - not read by the composer or the catalog builder.</summary>
        [JsonPropertyName("creatureType")]
        public string CreatureType { get; set; }

        /// <summary>Informational only, e.g. "retail" or "custom" - not read by the composer or the catalog builder.</summary>
        [JsonPropertyName("source")]
        public string Source { get; set; }

        [JsonPropertyName("members")]
        public List<SpeciesMemberDef> Members { get; set; } = new List<SpeciesMemberDef>();

        public const double MinStretchOverscale = 1.0, MaxStretchOverscale = 4.0;

        /// <summary>Family-stretch opt-out: false excludes this table's family from stretching. Absent means it stretches.</summary>
        [JsonPropertyName("stretch")]
        public bool? Stretch { get; set; }

        /// <summary>Health and damage (and XP) multiplier for stretched members, valid in [1, 4]. Absent means 1.0.</summary>
        [JsonPropertyName("stretchOverscale")]
        public double? StretchOverscale { get; set; }

        /// <summary>
        /// Draw opt-out: false removes this table from every family draw - Threads (DungeonRosterSelector.IsEligible, family
        /// stretch) and World Events (the catalog merge and the family registration) - while the table stays in the roster for
        /// band statistics, so the band standard, health median and authored-top pivot do not move. Absent means drawable.
        /// Carried across regeneration by tools/we-species-tables/generate_species_tables.py (CURATED_KEYS).
        /// </summary>
        [JsonPropertyName("enabled")]
        public bool? Enabled { get; set; }

        [JsonIgnore]
        public bool Drawable => Enabled != false;

        [JsonIgnore]
        public bool Stretches => Stretch != false;

        [JsonIgnore]
        public double Overscale => StretchOverscale ?? 1.0;
    }
}
