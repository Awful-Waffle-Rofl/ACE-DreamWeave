using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using ACE.Database;
using ACE.Entity.Models;
using ACE.Server.WorldObjects;

namespace ACE.Server.SalvageRoller
{
    /// <summary>
    /// The item snapshot format of the Salvage Roller's audit trail (Docs/SalvageRoller/DESIGN.md 5.9). A refund is
    /// rebuilt from the audit rows ALONE, so an item's snapshot is its WHOLE runtime biota: every property table,
    /// instance ids included (AllowedWielder, the original owner and so on, which DuoGemSnapshot deliberately drops),
    /// positions included, emotes, create lists, generators, book pages, enchantments, attributes and skills.
    ///
    /// Shape: { "format": "SalvageRollerFullBiota", "v": 1, "src": guid, "wcid": wcid, "biota": { ...ACE.Entity.Models.Biota... } }.
    /// <see cref="Deserialize"/> turns "biota" back into a runtime Biota. The one thing left out is each emote's
    /// back-reference to its owning Weenie (PropertiesEmote.Object), which is not item data and would recurse.
    /// </summary>
    internal static class SalvageRollerAuditSnapshot
    {
        internal const string Format = "SalvageRollerFullBiota";

        internal const int Version = 1;

        internal static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            WriteIndented = false,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };

        private static readonly PropertyInfo[] BiotaProperties = typeof(Biota)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0)
            .ToArray();

        /// <summary>The snapshot JSON for a runtime biota. Runtime ACE.Entity.Models.Biota only. Null for a null biota.</summary>
        internal static string Serialize(Biota biota)
        {
            var node = SerializeToNode(biota);

            return node?.ToJsonString();
        }

        /// <summary>The snapshot of a live object, read under its biota lock.</summary>
        internal static JsonObject SerializeToNode(WorldObject item)
        {
            if (item?.Biota == null)
                return null;

            item.BiotaDatabaseLock.EnterReadLock();

            try
            {
                return SerializeToNode(item.Biota);
            }
            finally
            {
                item.BiotaDatabaseLock.ExitReadLock();
            }
        }

        internal static JsonObject SerializeToNode(Biota biota)
        {
            if (biota == null)
                return null;

            return new JsonObject
            {
                ["format"] = Format,
                ["v"] = Version,
                ["src"] = biota.Id,
                ["wcid"] = biota.WeenieClassId,
                ["biota"] = JsonSerializer.SerializeToNode(Detached(biota), Options),
            };
        }

        /// <summary>The runtime biota a snapshot carries, or null when the node is not one of ours.</summary>
        internal static Biota Deserialize(JsonNode snapshot)
        {
            if (snapshot is not JsonObject obj || (string)obj["format"] != Format || obj["biota"] == null)
                return null;

            return obj["biota"].Deserialize<Biota>(Options);
        }

        /// <summary>
        /// A shallow copy of every settable public table (found by reflection, so a table added to Biota later is
        /// carried without anyone remembering to list it here), with each emote cloned free of its Weenie back-reference.
        /// </summary>
        private static Biota Detached(Biota biota)
        {
            var copy = new Biota();

            foreach (var property in BiotaProperties)
                property.SetValue(copy, property.GetValue(biota));

            if (biota.PropertiesEmote != null)
            {
                copy.PropertiesEmote = biota.PropertiesEmote.Select(e =>
                {
                    var clone = e.Clone();
                    clone.DatabaseRecordId = e.DatabaseRecordId;
                    return clone;
                }).ToList();
            }

            return copy;
        }

        /// <summary>
        /// Hands <see cref="Serialize"/> to the character purge in ACE.Database, which cannot reference this
        /// assembly. Called at startup before the purge runs.
        /// </summary>
        internal static void InstallPurgeSerializer()
        {
            ShardDatabaseOfflineTools.SalvageRollerItemSnapshotSerializer = Serialize;
        }
    }
}