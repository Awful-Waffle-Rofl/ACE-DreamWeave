using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ACE.Server.WorldEvents.Defs
{
    /// <summary>
    /// One member of a family's roster, discovered either by the weenie-scan catalog builder
    /// (TECH-DESIGN 2.3, C2) or joined in from a species reference table (TECH-DESIGN C15, 2026-08-16).
    /// <see cref="FromTable"/> distinguishes the two; either way Name/Level always come from the live
    /// weenie, never from JSON.
    /// </summary>
    public class FamilyMember
    {
        public uint Wcid;
        public string Name;
        public int Level;
        public int Role;

        /// <summary>True when this member was joined in from a species table rather than found by the
        /// WorldEventCreature flag scan (TECH-DESIGN C15). Default false for scan members.</summary>
        public bool FromTable;

        /// <summary>
        /// True when the live weenie carries a non-empty spellbook, i.e. this member can cast. Set by
        /// <see cref="ACE.Server.WorldEvents.WorldEventCatalogBuilder"/> on BOTH catalog paths (flag scan
        /// and species table) from the weenie already in hand, so it costs no extra database read.
        ///
        /// A monster casts iff its spellbook is non-empty: Monster_Combat's attack roll only reaches the
        /// magic branch behind HasKnownSpells (Monster_Combat.cs:108), and HasKnownSpells is
        /// Biota.HasKnownSpell (Monster_Magic.cs:52), whose contents come straight from the weenie's
        /// PropertiesSpellBook (WeenieConverter.cs:91-95).
        /// </summary>
        public bool Caster;

        /// <summary>
        /// The catalog family this member was filed under - the WorldEventFamily flag value, or the
        /// species table's id. Carried on the member itself so a UNION roster built from two families
        /// (WorldEventComposition.Roster) can still say where each member came from.
        /// </summary>
        public string FamilyId;
    }

    /// <summary>
    /// One entry from Content/events/axes/families.json (TECH-DESIGN 5.4). Metadata only, per C2 -
    /// roster membership (which wcids, which band, which role) comes from the weenie-scan catalog,
    /// and (per C15) from species reference tables under Content/events/axes/species/, never from
    /// this file.
    /// </summary>
    public class FamilyDef
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("displayName")]
        public string DisplayName { get; set; }

        [JsonPropertyName("hueKey")]
        public string HueKey { get; set; }

        [JsonPropertyName("biomeTags")]
        public List<string> BiomeTags { get; set; } = new List<string>();

        /// <summary>
        /// Roster slot filled later by the WP-02 catalog scan. NEVER read from JSON - see class remarks.
        /// </summary>
        [JsonIgnore]
        public List<FamilyMember> Members { get; set; } = new List<FamilyMember>();
    }
}
