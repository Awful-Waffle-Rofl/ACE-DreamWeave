using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using ACE.Common.Extensions;
using ACE.Database.Adapter;
using ACE.Database.Models.Shard;
using ACE.Entity.Enum.Properties;

namespace ACE.Database
{
    /// <summary>
    /// The Salvage Roller's share of the offline purge tools (Docs/SalvageRoller/DESIGN.md 5.7 and 5.9).
    /// </summary>
    public static partial class ShardDatabaseOfflineTools
    {
        /// <summary>
        /// account_vault.kind of a Salvage Roller escrow container. The same value as ACE.Server's
        /// AccountVaultStore.EscrowContainerKind, which this assembly cannot reference; SalvageRollerSchemaTests pins
        /// the two equal.
        /// </summary>
        public const int SalvageRollerEscrowContainerKind = 2;

        private static uint? InstanceId(ACE.Entity.Models.Biota runtime, PropertyInstanceId key) =>
            runtime.PropertiesIID != null && runtime.PropertiesIID.TryGetValue(key, out var value) && value != 0 ? value : (uint?)null;

        /// <summary>
        /// Serializes an escrowed item for the CharacterPurged audit row, in the same snapshot format the
        /// rest of the Salvage Roller's audit trail uses (ACE.Server's SalvageRollerAuditSnapshot: the whole
        /// runtime biota, every property table including instance ids). Installed by
        /// ACE.Server at startup, before the purge runs, because that format lives above this assembly.
        /// Returns null when it cannot serialize the item.
        ///
        /// When it is unset or returns null, <see cref="SnapshotForAudit"/> falls back to serializing the
        /// runtime Biota directly, so the audit row is never written without the item's properties.
        /// </summary>
        public static Func<ACE.Entity.Models.Biota, string> SalvageRollerItemSnapshotSerializer;

        private static readonly JsonSerializerOptions FallbackSnapshotOptions = new JsonSerializerOptions
        {
            WriteIndented = false,
            ReferenceHandler = ReferenceHandler.IgnoreCycles,
        };

        /// <summary>
        /// Stages, inside <paramref name="context"/>, the deletion of the character's open Salvage Roller
        /// batch: the biota of every object it holds in escrow, its item rows, the batch row - and the
        /// CharacterPurged audit row carrying a full snapshot of each object, so the record outlives them.
        /// Nothing is saved here: the caller's single SaveChanges commits all of it together with the
        /// character purge, or none of it.
        ///
        /// <paramref name="alreadyRemoved"/> holds every biota id the caller has already staged for deletion
        /// (the character's own pack and wielded items, and the player biota). An escrowed guid found there
        /// is not staged twice; that is the case of a take whose delivery landed (the item is back in the
        /// pack) before the batch rows were deleted.
        ///
        /// Any other escrowed guid is deleted ONLY while it is still in escrow: no parent at all, or inside a
        /// kind-2 account_vault container of the batch's account. An object that is wielded, or inside any other
        /// container (another character's pack, say), has left escrow and is left alone; its audit entry says so.
        ///
        /// Returns how many escrowed biotas it staged for deletion.
        ///
        /// Throws if the tables cannot be read. The caller must then abandon the whole character purge
        /// rather than purge the character and leave its batch half known.
        /// </summary>
        internal static int StageSalvageBatchPurge(ShardDbContext context, uint characterId, Character character, HashSet<uint> alreadyRemoved, string reason)
        {
            var batch = context.CharacterSalvageBatch.FirstOrDefault(b => b.CharacterId == characterId);

            if (batch == null)
                return 0;

            var items = context.CharacterSalvageBatchItem.Where(i => i.BatchId == batch.Id).ToList();

            var shard = new ShardDatabase();
            var itemsJson = new JsonArray();
            var staged = 0;

            foreach (var item in items)
            {
                var role = (SalvageBatchItemRole)item.Role;

                var entry = new JsonObject
                {
                    ["guid"] = item.Guid,
                    ["guidHex"] = $"0x{item.Guid:X8}",
                    ["role"] = role.ToString(),
                    ["roleCode"] = item.Role,
                };

                if (alreadyRemoved.Contains(item.Guid))
                {
                    entry["outcome"] = "deleted with the character's own possessions: it was in the pack or wielded, not in escrow";
                }
                else
                {
                    var biota = shard.GetBiotaWithCollections(context, item.Guid);

                    if (biota == null)
                    {
                        entry["outcome"] = "no biota exists for this guid; nothing was deleted (a Hammer already consumed, or an object that was never saved)";
                    }
                    else
                    {
                        var runtime = BiotaConverter.ConvertToEntityBiota(biota);

                        entry["wcid"] = runtime.WeenieClassId;
                        entry["name"] = runtime.PropertiesString != null && runtime.PropertiesString.TryGetValue(PropertyString.Name, out var name) ? name : null;
                        entry["structure"] = runtime.PropertiesInt != null && runtime.PropertiesInt.TryGetValue(PropertyInt.Structure, out var structure) ? (int?)structure : null;
                        entry["maxStructure"] = runtime.PropertiesInt != null && runtime.PropertiesInt.TryGetValue(PropertyInt.MaxStructure, out var maxStructure) ? (int?)maxStructure : null;

                        var container = InstanceId(runtime, PropertyInstanceId.Container);
                        var wielder = InstanceId(runtime, PropertyInstanceId.Wielder);

                        entry["container"] = container.HasValue ? $"0x{container.Value:X8}" : null;
                        entry["wielder"] = wielder.HasValue ? $"0x{wielder.Value:X8}" : null;

                        var (format, snapshot) = SnapshotForAudit(runtime);

                        entry["snapshotFormat"] = format;
                        entry["snapshot"] = snapshot;

                        // ONLY an object still in this account's escrow is this batch's to delete: parentless (between
                        // two writes) or inside a kind-2 account_vault container of the batch's account. Anything
                        // else has left escrow - a take that delivered it, then a trade or a give - and now belongs
                        // to somebody else, so it is recorded and left alone.
                        var inEscrow = wielder == null && (container == null ||
                            context.AccountVault.Any(v => v.ContainerGuid == container.Value && v.Kind == SalvageRollerEscrowContainerKind && v.AccountId == batch.AccountId));

                        if (inEscrow)
                        {
                            entry["outcome"] = "deleted from escrow by the character purge";

                            context.Biota.Remove(biota);
                            alreadyRemoved.Add(item.Guid);
                            staged++;
                        }
                        else
                        {
                            entry["outcome"] = wielder != null
                                ? $"had left escrow (wielded by 0x{wielder.Value:X8}); not deleted"
                                : $"had left escrow (container 0x{container.Value:X8}); not deleted";
                        }
                    }
                }

                itemsJson.Add(entry);

                context.CharacterSalvageBatchItem.Remove(item);
            }

            var state = (SalvageBatchState)batch.State;
            var action = (SalvageBatchAction)batch.Action;

            var detail = new JsonObject
            {
                ["event"] = SalvageRollerAuditEvent.CharacterPurged.ToString(),
                ["reason"] = string.IsNullOrWhiteSpace(reason) ? "deleted character purged" : reason,
                ["character"] = new JsonObject
                {
                    ["id"] = characterId,
                    ["idHex"] = $"0x{characterId:X8}",
                    ["name"] = character?.Name,
                    ["accountId"] = character?.AccountId ?? batch.AccountId,
                },
                ["batch"] = new JsonObject
                {
                    ["id"] = batch.Id,
                    ["accountId"] = batch.AccountId,
                    ["itemGuid"] = batch.ItemGuid,
                    ["itemGuidHex"] = $"0x{batch.ItemGuid:X8}",
                    ["itemWcid"] = batch.ItemWcid,
                    ["action"] = action.ToString(),
                    ["actionCode"] = batch.Action,
                    ["rollCount"] = batch.RollCount,
                    ["chosenSeq"] = batch.ChosenSeq,
                    ["createdAtUtc"] = batch.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"),
                    ["payload"] = batch.Payload,
                },
                ["stateBefore"] = state.ToString(),
                ["stateBeforeCode"] = batch.State,
                ["stateAfter"] = "deleted: the batch row, its item rows and every object in items whose outcome says deleted are removed in the same commit as the character; an object whose outcome says it had left escrow is untouched",
                ["items"] = itemsJson,
            };

            context.CharacterSalvageBatch.Remove(batch);

            context.SalvageRollerAudit.Add(new SalvageRollerAudit
            {
                BatchId = batch.Id,
                CharacterId = characterId,
                AccountId = character?.AccountId ?? batch.AccountId,
                CharacterName = string.IsNullOrEmpty(character?.Name) ? "<unknown>" : character.Name,
                Event = (byte)SalvageRollerAuditEvent.CharacterPurged,
                ItemGuid = batch.ItemGuid,
                CreatedAt = DateTime.UtcNow,
                Detail = detail.ToJsonString(),
            });

            log.Info($"[SALVAGE ROLLER] Character 0x{characterId:X8} purge: staging deletion of batch {batch.Id} ({state}, {action}), {items.Count} item row(s), {staged} escrowed biota(s). A CharacterPurged audit row records every one.");

            return staged;
        }

        /// <summary>
        /// A self-contained snapshot of <paramref name="runtime"/> for an audit row: the installed
        /// serializer's output when it produces one, otherwise the runtime Biota serialized directly.
        /// Never returns a null snapshot.
        /// </summary>
        internal static (string Format, JsonNode Snapshot) SnapshotForAudit(ACE.Entity.Models.Biota runtime)
        {
            var serializer = SalvageRollerItemSnapshotSerializer;

            if (serializer != null)
            {
                try
                {
                    var json = serializer(runtime);

                    if (!string.IsNullOrEmpty(json))
                        return ("SalvageRollerFullBiota", JsonNode.Parse(json));
                }
                catch (Exception ex)
                {
                    log.Error($"[SALVAGE ROLLER] the installed snapshot serializer failed for 0x{runtime.Id:X8}; falling back to the raw biota: {ex.GetFullMessage()}");
                }
            }

            try
            {
                return ("ACE.Entity.Models.Biota", JsonNode.Parse(JsonSerializer.Serialize(runtime, FallbackSnapshotOptions)));
            }
            catch (Exception ex)
            {
                log.Error($"[SALVAGE ROLLER] could not serialize 0x{runtime.Id:X8} for its audit row: {ex.GetFullMessage()}");

                return ("unavailable", JsonValue.Create($"serialization failed: {ex.Message}"));
            }
        }
    }
}
