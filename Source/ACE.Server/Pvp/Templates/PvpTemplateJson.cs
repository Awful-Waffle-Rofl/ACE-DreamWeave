using System;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ACE.Server.Pvp.Templates
{
    /// <summary>
    /// Serialization for the two template shapes, each inside a versioned envelope:
    ///
    ///   definition: {"v":1,"def":{...}}      restore record: {"v":1,"rec":{...}}
    ///
    /// A reader refuses (returns false) on a schema version it does not know rather than guessing, because
    /// both shapes drive mutation: a misread definition writes the wrong build onto a player, and a misread
    /// record restores the wrong one. Nothing here throws.
    ///
    /// THE RECORD SIZE CAP. PropertyString values live in a TEXT column (64 KB). <see cref="MaxRecordBytes"/>
    /// is 60 KB of UTF-8, leaving headroom, and <see cref="TrySerializeRecord"/> refuses above it, so a record
    /// that would be truncated by the database is never written (TEMPLATES.md "Restore record lives on the
    /// biota"). The apply checks this BEFORE touching anything.
    /// </summary>
    public static class PvpTemplateJson
    {
        public const int DefinitionSchemaVersion = 1;

        public const int RecordSchemaVersion = 1;

        /// <summary>60 KB of UTF-8: the largest restore record the apply will write.</summary>
        public const int MaxRecordBytes = 60 * 1024;

        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };

        private class DefinitionEnvelope
        {
            [JsonPropertyName("v")] public int Version { get; set; }
            [JsonPropertyName("def")] public PvpTemplateDefinition Definition { get; set; }
        }

        private class RecordEnvelope
        {
            [JsonPropertyName("v")] public int Version { get; set; }
            [JsonPropertyName("rec")] public PvpTemplateRestoreRecord Record { get; set; }
        }

        public static string SerializeDefinition(PvpTemplateDefinition definition)
            => JsonSerializer.Serialize(new DefinitionEnvelope { Version = DefinitionSchemaVersion, Definition = definition }, Options);

        /// <summary>False, with a reason, for null/blank, malformed JSON, an unknown schema version, or no key.</summary>
        public static bool TryDeserializeDefinition(string json, out PvpTemplateDefinition definition, out string error)
        {
            definition = null;
            error = null;

            if (string.IsNullOrWhiteSpace(json))
            {
                error = "the definition is empty";
                return false;
            }

            DefinitionEnvelope envelope;

            try
            {
                envelope = JsonSerializer.Deserialize<DefinitionEnvelope>(json, Options);
            }
            catch (Exception ex)
            {
                error = $"the definition is not valid JSON ({ex.Message})";
                return false;
            }

            if (envelope == null || envelope.Definition == null)
            {
                error = "the definition has no body";
                return false;
            }

            if (envelope.Version != DefinitionSchemaVersion)
            {
                error = $"the definition has schema version {envelope.Version}, this server reads {DefinitionSchemaVersion}";
                return false;
            }

            if (string.IsNullOrEmpty(envelope.Definition.Key))
            {
                error = "the definition has no key";
                return false;
            }

            definition = envelope.Definition;
            Normalize(definition);
            return true;
        }

        /// <summary>
        /// Serializes a restore record, refusing (false, json null) when it exceeds <see cref="MaxRecordBytes"/>.
        /// <paramref name="byteCount"/> is the UTF-8 size either way, for the refusal message.
        /// </summary>
        public static bool TrySerializeRecord(PvpTemplateRestoreRecord record, out string json, out int byteCount)
        {
            var text = JsonSerializer.Serialize(new RecordEnvelope { Version = RecordSchemaVersion, Record = record }, Options);

            byteCount = Encoding.UTF8.GetByteCount(text);

            if (byteCount > MaxRecordBytes)
            {
                json = null;
                return false;
            }

            json = text;
            return true;
        }

        /// <summary>False, with a reason, for null/blank, malformed JSON or an unknown schema version.</summary>
        public static bool TryDeserializeRecord(string json, out PvpTemplateRestoreRecord record, out string error)
        {
            record = null;
            error = null;

            if (string.IsNullOrWhiteSpace(json))
            {
                error = "the restore record is empty";
                return false;
            }

            RecordEnvelope envelope;

            try
            {
                envelope = JsonSerializer.Deserialize<RecordEnvelope>(json, Options);
            }
            catch (Exception ex)
            {
                error = $"the restore record is not valid JSON ({ex.Message})";
                return false;
            }

            if (envelope == null || envelope.Record == null)
            {
                error = "the restore record has no body";
                return false;
            }

            if (envelope.Version != RecordSchemaVersion)
            {
                error = $"the restore record has schema version {envelope.Version}, this server reads {RecordSchemaVersion}";
                return false;
            }

            record = envelope.Record;
            Normalize(record);
            return true;
        }

        /// <summary>A JSON null for a collection reads back as null; every reader downstream expects empty instead.</summary>
        private static void Normalize(PvpTemplateDefinition d)
        {
            d.Attributes ??= new();
            d.Vitals ??= new();
            d.Skills ??= new();
            d.PowerInts ??= new();
            d.Buffs ??= new();
            d.Spells ??= new();
            d.Kit ??= new();

            foreach (var item in d.Kit)
            {
                if (item == null)
                    continue;

                item.Ints ??= new();
                item.Int64s ??= new();
                item.Bools ??= new();
                item.Floats ??= new();
                item.Strings ??= new();
                item.DataIds ??= new();
                item.SpellBook ??= new();
                item.AnimParts ??= new();
                item.Palettes ??= new();
                item.TextureMaps ??= new();
                item.Enchantments ??= new();
            }

            d.Kit.RemoveAll(i => i == null);
            d.Skills.RemoveAll(s => s == null);
            d.Buffs.RemoveAll(b => b == null);
        }

        private static void Normalize(PvpTemplateRestoreRecord r)
        {
            r.Attributes ??= new();
            r.Vitals ??= new();
            r.Skills ??= new();
            r.Ints ??= new();
            r.RemovedEnchantments ??= new();
            r.AddedSpells ??= new();
            r.TemplateSpells ??= new();
            r.OwnEquip ??= new();

            r.Skills.RemoveAll(s => s == null);
            r.RemovedEnchantments.RemoveAll(e => e == null);
            r.OwnEquip.RemoveAll(e => e == null);
        }
    }
}
