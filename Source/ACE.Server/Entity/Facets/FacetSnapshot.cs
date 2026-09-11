using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;

namespace ACE.Server.Entity.Facets
{
    /// <summary>One skill's stored state inside a facet. Mirrors PropertiesSkill's four mutable fields.</summary>
    public class FacetSkillEntry
    {
        [JsonPropertyName("s")]
        public Skill Skill { get; set; }

        [JsonPropertyName("sac")]
        public SkillAdvancementClass Sac { get; set; }

        [JsonPropertyName("r")]
        public ushort Ranks { get; set; }

        [JsonPropertyName("pp")]
        public uint Pp { get; set; }

        [JsonPropertyName("il")]
        public uint InitLevel { get; set; }
    }

    /// <summary>
    /// One remembered worn item. Both identifiers are stored deliberately: the account vault collapses
    /// any item provably identical to its weenie template into a ledger row and DESTROYS its biota,
    /// rebuilding a new object with a new guid on withdraw, so the guid alone rots silently.
    /// </summary>
    public class FacetEquipEntry
    {
        [JsonPropertyName("guid")]
        public uint Guid { get; set; }

        [JsonPropertyName("wcid")]
        public uint Wcid { get; set; }

        /// <summary>The EquipMask the item was worn in, as its raw integer value.</summary>
        [JsonPropertyName("slot")]
        public int Slot { get; set; }
    }

    /// <summary>
    /// Serialization for the four character_facet JSON columns.
    ///
    /// No deserializer ever THROWS. These blobs are the only record of a build the player is not
    /// currently standing on, so a throw on a malformed column would strand that facet permanently.
    /// This is the same contract MarketSnapshot takes, for the same reason.
    ///
    /// What a failed parse degrades TO depends on whether the caller is about to mutate. The plain
    /// Deserialize* readers return an EMPTY collection, which is right for a display and for the two
    /// columns where empty is a self-consistent build. Skills also have a
    /// <see cref="TryDeserializeSkills"/> form that REFUSES instead, because an empty skill list is not a
    /// harmless answer on the switch path - it releases the outgoing build's experience without
    /// committing any of it back. See that method's remarks.
    ///
    /// Attributes are a THIRD degrade rule, not a copy of either: <see cref="TryDeserializeAttributes"/>
    /// returns false for anything it cannot read as a complete six-attribute arrangement, and a false
    /// there means "keep the character's LIVE arrangement", which conserves by construction. It is not a
    /// refusal (that would strand a slot written before the column existed) and it is not an empty
    /// degrade (an empty arrangement would silently delete the player's redistribution).
    ///
    /// Abilities are keyed by ability NAME, never by the ClassAbilityId enum value, so a future enum
    /// reordering cannot silently repoint a stored facet at a different ability.
    /// </summary>
    public static class FacetSnapshot
    {
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            WriteIndented = false,
        };

        public static string SerializeSkills(IEnumerable<FacetSkillEntry> skills)
            => JsonSerializer.Serialize(skills ?? new List<FacetSkillEntry>(), Options);

        /// <summary>
        /// The fail-open read, for ADVISORY callers only (FacetCommands.HandleList's slot listing). A
        /// malformed or blank column yields an empty list, which for a display is a cosmetically wrong
        /// line and nothing worse.
        ///
        /// A caller for whom an empty list would drive a MUTATION must use
        /// <see cref="TryDeserializeSkills"/> instead - see its remarks for the XP hole that opens
        /// otherwise. This is the same load-bearing-versus-advisory split
        /// Player.TryGetCharacterFacetRows documents one layer up.
        /// </summary>
        public static List<FacetSkillEntry> DeserializeSkills(string json)
            => Parse<List<FacetSkillEntry>>(json) ?? new List<FacetSkillEntry>();

        /// <summary>
        /// The LOAD-BEARING read of skills_Json. Returns FALSE, with <paramref name="skills"/> null, when
        /// the column could not be read as a real build: null, blank/whitespace, or malformed JSON.
        ///
        /// Why skills are the one column that cannot degrade to empty, when abilities and equip both can:
        /// a facet switch RELEASES all of the outgoing build's invested PP into AvailableExperience and
        /// then COMMITS the incoming build's PP back out of it. An empty incoming list makes that second
        /// number zero, so a blank or corrupt skills_Json hands the player their entire outgoing build's
        /// experience as unspent XP. The empty degrade is self-consistent for abilities (an empty set means
        /// "unlearn everything", and ApplyFacetAbilities erases exactly what the incoming set does not
        /// name) and harmless for equip (nothing is re-equipped), so both keep the fail-open form.
        ///
        /// A GENUINELY empty build is still representable and still returns TRUE: SerializeSkills always
        /// writes at least "[]", which parses to an empty list. Only the absence of any parseable value
        /// is a refusal.
        ///
        /// Null is treated as a refusal along with blank, even though character_facet.skills_Json is NOT
        /// NULL so a null should be unreachable: the safe answer to "this column holds nothing" does not
        /// depend on which flavour of nothing it holds, and a caller that mutates on the answer must not
        /// have a reachable-looking path that fails open.
        /// </summary>
        public static bool TryDeserializeSkills(string json, out List<FacetSkillEntry> skills)
        {
            skills = string.IsNullOrWhiteSpace(json) ? null : Parse<List<FacetSkillEntry>>(json);

            return skills != null;
        }

        public static string SerializeAbilities(IReadOnlyDictionary<string, int> abilities)
            => JsonSerializer.Serialize(abilities ?? new Dictionary<string, int>(), Options);

        public static Dictionary<string, int> DeserializeAbilities(string json)
            => Parse<Dictionary<string, int>>(json) ?? new Dictionary<string, int>();

        /// <summary>
        /// The six primary attributes' InitLevel, as a bare object keyed by attribute NAME:
        ///
        ///   {"Strength":100,"Endurance":100,"Coordination":100,"Quickness":10,"Focus":10,"Self":10}
        ///
        /// Name-keyed for the same stated reason abilities_Json is: a future PropertyAttribute
        /// reordering cannot silently repoint a stored value at a different attribute.
        ///
        /// Deliberately NOT folded into skills_Json. That column is a bare JSON array in every
        /// already-shipped row, so widening it would have to rewrite live data, and it carries a
        /// load-bearing REFUSAL whose degrade rule is the opposite of this one's: an unreadable
        /// skills_Json must refuse the switch, while an unreadable attrs_Json must keep the character's
        /// live arrangement (which is conserving by construction - see
        /// <see cref="TryDeserializeAttributes"/>).
        /// </summary>
        public static string SerializeAttributes(IReadOnlyDictionary<PropertyAttribute, uint> attributes)
        {
            var byName = new Dictionary<string, uint>();

            if (attributes != null)
            {
                foreach (var pair in attributes)
                    byName[pair.Key.ToString()] = pair.Value;
            }

            return JsonSerializer.Serialize(byName, Options);
        }

        /// <summary>
        /// Reads attrs_Json back. Returns FALSE, with <paramref name="attributes"/> null, unless the
        /// column parses AND names all six primary attributes.
        ///
        /// FOUR degrade cases, all the same answer: null (the pre-upgrade case - the column is NULLABLE
        /// precisely so that state is representable), blank, malformed JSON, and a parseable object that
        /// is MISSING one of the six. The last of those is not pedantry: the caller's whole guarantee is
        /// that the sum of what it writes equals the sum of what was there, and a partial arrangement
        /// would leave the unnamed attributes at their live values while the reconciliation had already
        /// balanced the surplus against a short stored sum. Refusing the parse instead makes the caller
        /// keep the live arrangement whole, which conserves trivially.
        ///
        /// What a FALSE means to the caller is therefore "keep the live arrangement", NOT "refuse the
        /// switch" - the exact opposite of <see cref="TryDeserializeSkills"/>, and the reason these two
        /// columns cannot share one reader. Nothing is lost by keeping live values: the outgoing row is
        /// captured from the live character on every switch, so the next switch away rewrites this
        /// column correctly.
        ///
        /// Keys the object carries beyond the six (an attribute that does not exist, a typo, a future
        /// addition) are IGNORED rather than rejected - only the six are read, and an unknown key cannot
        /// affect the sum of what is applied.
        ///
        /// Never throws, matching this class's contract.
        /// </summary>
        public static bool TryDeserializeAttributes(string json, out Dictionary<PropertyAttribute, uint> attributes)
        {
            attributes = null;

            var byName = Parse<Dictionary<string, uint>>(json);

            if (byName == null)
                return false;

            var parsed = new Dictionary<PropertyAttribute, uint>();

            foreach (var attribute in FacetAttributes.PrimaryAttributes)
            {
                if (!byName.TryGetValue(attribute.ToString(), out var value))
                    return false;

                parsed[attribute] = value;
            }

            attributes = parsed;
            return true;
        }

        public static string SerializeEquip(IEnumerable<FacetEquipEntry> equip)
            => JsonSerializer.Serialize(equip ?? new List<FacetEquipEntry>(), Options);

        public static List<FacetEquipEntry> DeserializeEquip(string json)
            => Parse<List<FacetEquipEntry>>(json) ?? new List<FacetEquipEntry>();

        private static T Parse<T>(string json) where T : class
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;

            try
            {
                return JsonSerializer.Deserialize<T>(json, Options);
            }
            catch (Exception)
            {
                // Deliberately swallowed - see the class summary. The caller gets an empty collection.
                return null;
            }
        }
    }
}
