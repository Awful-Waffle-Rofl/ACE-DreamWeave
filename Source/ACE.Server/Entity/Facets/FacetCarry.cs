using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

using ACE.Database.Models.Shard;

namespace ACE.Server.Entity.Facets
{
    /// <summary>
    /// Carries a character's stored Player Facets (`character_facet` rows) onto a COPY of that
    /// character. Pure and database-free so both copy paths share one rule and it is unit-testable:
    /// AdminCommands.DoCopyChar (@copychar, restore, tester roster refresh) and the offline
    /// prodcharseed extractor (ACE.Content.Tools).
    ///
    /// The only thing a copy has to rewrite is identity. The row's character_Id moves to the new
    /// guid, and every equip_Json entry's "guid" moves through the same old-item -> new-item map the
    /// copy built for the biotas, because a facet's remembered worn set is resolved by guid first
    /// (FacetGear) and a guid left pointing at the source character's item would resolve to nothing,
    /// or worse, to someone else's item.
    ///
    /// A guid with no entry in the map is left alone, never dropped: it names something that was not
    /// copied (a vault-held item, a skipped deed), and FacetGear's own fallback resolves such an entry
    /// by wcid against the vault ledger. Dropping the entry would lose that fallback.
    ///
    /// attrs_Json is copied verbatim INCLUDING null, which is load-bearing (see
    /// CharacterFacet.AttrsJson): null means "predates per-facet attributes" and must never become ""
    /// or "{}".
    /// </summary>
    public static class FacetCarry
    {
        // Relaxed escaping keeps a rewritten blob byte-close to what FacetSnapshot wrote (no ' for an
        // apostrophe in a future field). Both forms are valid JSON that FacetSnapshot reads back.
        private static readonly JsonSerializerOptions WriteOptions = new JsonSerializerOptions
        {
            WriteIndented = false,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        /// <summary>
        /// Rewrites the "guid" of every entry in an equip_Json array through <paramref name="guidMap"/>.
        /// Works on the JSON tree rather than FacetEquipEntry, so any key this code does not know about
        /// survives untouched. Returns the input string unchanged (same reference) when nothing was
        /// remapped or the text is not a JSON array - never throws, matching FacetSnapshot's contract.
        /// </summary>
        public static string RemapEquipJson(string equipJson, IReadOnlyDictionary<uint, uint> guidMap)
        {
            if (string.IsNullOrWhiteSpace(equipJson) || guidMap == null || guidMap.Count == 0)
                return equipJson;

            try
            {
                if (!(JsonNode.Parse(equipJson) is JsonArray array))
                    return equipJson;

                var changed = false;

                foreach (var node in array)
                {
                    if (!(node is JsonObject entry))
                        continue;

                    if (!entry.TryGetPropertyValue("guid", out var guidNode) || !(guidNode is JsonValue guidValue))
                        continue;

                    if (!guidValue.TryGetValue<long>(out var oldGuid) || oldGuid < 0 || oldGuid > uint.MaxValue)
                        continue;

                    if (!guidMap.TryGetValue((uint)oldGuid, out var newGuid) || newGuid == (uint)oldGuid)
                        continue;

                    entry["guid"] = JsonValue.Create(newGuid);
                    changed = true;
                }

                return changed ? array.ToJsonString(WriteOptions) : equipJson;
            }
            catch (Exception)
            {
                return equipJson;
            }
        }

        /// <summary>
        /// New rows for the copy: character_Id = <paramref name="newCharacterId"/>, equip guids remapped,
        /// every other column (slot, name, skills, abilities, attrs incl. null, updated_At) verbatim.
        /// Never mutates the input rows.
        /// </summary>
        public static List<CharacterFacet> Remap(IEnumerable<CharacterFacet> rows, uint newCharacterId, IReadOnlyDictionary<uint, uint> guidMap)
        {
            var result = new List<CharacterFacet>();

            if (rows == null)
                return result;

            foreach (var row in rows.Where(r => r != null).OrderBy(r => r.Slot))
            {
                result.Add(new CharacterFacet
                {
                    CharacterId = newCharacterId,
                    Slot = row.Slot,
                    Name = row.Name,
                    SkillsJson = row.SkillsJson,
                    AbilitiesJson = row.AbilitiesJson,
                    EquipJson = RemapEquipJson(row.EquipJson, guidMap),
                    AttrsJson = row.AttrsJson,
                    UpdatedAt = row.UpdatedAt,
                });
            }

            return result;
        }

        /// <summary>
        /// Hands each row to <paramref name="saver"/> (ShardDatabase.SaveCharacterFacet) and collects the
        /// slots that did not save. This is deliberately not FacetRowFetch.TrySaveAll: that one blocks the
        /// calling thread on a shared timeout waiting for queued-save confirmations, which is unusable on
        /// the shard worker (the confirmations are produced by that same worker). This takes a synchronous
        /// saver (ShardDatabase.SaveCharacterFacet via BaseDatabase) that returns its own result.
        /// A throwing or false-returning saver counts as a failure for that row
        /// and does not stop the remaining rows. Returns the number saved.
        /// </summary>
        public static int SaveAll(IEnumerable<CharacterFacet> rows, Func<CharacterFacet, bool> saver, List<byte> failedSlots)
        {
            var saved = 0;

            foreach (var row in rows ?? Enumerable.Empty<CharacterFacet>())
            {
                bool ok;
                try
                {
                    ok = saver(row);
                }
                catch (Exception)
                {
                    ok = false;
                }

                if (ok)
                    saved++;
                else
                    failedSlots?.Add(row.Slot);
            }

            return saved;
        }
    }
}
