using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

using ACE.Database.Models.Shard;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;

namespace ACE.Server.SalvageRoller
{
    /// <summary>
    /// The Salvage Roller's durable audit trail (DESIGN 5.9). One row is written BEFORE each step that moves value, in
    /// its own SaveChanges (ShardDatabase.AddSalvageRollerAudit), and a failed write refuses that step. Every detail is
    /// complete on its own: the character, the account, the batch row as it stands, the state before and after, and
    /// the WHOLE biota of every item involved (<see cref="SalvageRollerAuditSnapshot"/>: every property table, instance
    /// ids included), so a refund can be rebuilt from the rows alone with no other lookup.
    /// </summary>
    internal static class SalvageRollerAuditTrail
    {
        /// <summary>
        /// Writes one row. True when it landed. On a failure the row's whole detail goes to the ERROR log under
        /// <see cref="SalvageRollerManager.RefundPrefix"/>, so nothing about the refused step is lost, and the caller
        /// must not take the step.
        /// </summary>
        internal static bool Write(ISalvageRollerHost host, SalvageRollerAuditEvent ev, CharacterSalvageBatch batch, uint? itemGuid, JsonObject detail)
        {
            detail ??= new JsonObject();

            var stamped = new JsonObject
            {
                ["event"] = ev.ToString(),
                ["writtenAtUtc"] = DateTime.UtcNow.ToString("o"),
                ["character"] = new JsonObject { ["id"] = host.CharacterId, ["hex"] = $"0x{host.CharacterId:X8}", ["name"] = host.CharacterName },
                ["account"] = host.AccountId,
                ["batch"] = BatchJson(batch),
            };

            foreach (var kvp in detail.ToList())
            {
                detail.Remove(kvp.Key);
                stamped[kvp.Key] = kvp.Value;
            }

            var json = stamped.ToJsonString();

            var row = new SalvageRollerAudit
            {
                BatchId = batch?.Id > 0 ? batch.Id : (ulong?)null,
                CharacterId = host.CharacterId,
                AccountId = host.AccountId,
                CharacterName = host.CharacterName,
                Event = (byte)ev,
                ItemGuid = itemGuid ?? batch?.ItemGuid,
                CreatedAt = DateTime.UtcNow,
                Detail = json,
            };

            bool written;

            try
            {
                written = host.AddAudit(row);
            }
            catch (Exception ex)
            {
                SalvageRollerManager.Log.Error($"{SalvageRollerManager.RefundPrefix} the {ev} audit write threw: {ex.Message}");
                written = false;
            }

            if (written)
            {
                SalvageRollerManager.Log.Info($"[SALVAGE ROLLER] {host.CharacterName} (0x{host.CharacterId:X8}) batch {batch?.Id.ToString() ?? "-"}: {ev}, item 0x{row.ItemGuid ?? 0:X8}.");
                return true;
            }

            SalvageRollerManager.Log.Error($"{SalvageRollerManager.RefundPrefix} {host.CharacterName} (0x{host.CharacterId:X8}) batch {batch?.Id.ToString() ?? "-"}, item 0x{row.ItemGuid ?? 0:X8}: the {ev} audit row could not be written, so that step was refused. Detail: {json}");
            return false;
        }

        /// <summary>
        /// Something needs staff: logs an ERROR under the refund prefix with the self-contained detail, then writes a
        /// best-effort Error audit row carrying the same detail, <paramref name="what"/> and the exception. The row is
        /// a record, never a gate: a failed write is logged and the caller carries on exactly as it would have.
        /// </summary>
        internal static void LogRefund(ISalvageRollerHost host, CharacterSalvageBatch batch, string what, JsonObject detail, Exception exception = null)
        {
            var json = detail?.ToJsonString() ?? "{}";

            if (exception != null)
                SalvageRollerManager.Log.Error($"{SalvageRollerManager.RefundPrefix} {host.CharacterName} (0x{host.CharacterId:X8}), account {host.AccountId}, batch {batch?.Id.ToString() ?? "-"}, item 0x{batch?.ItemGuid ?? 0:X8}: {what} Detail: {json}", exception);
            else
                SalvageRollerManager.Log.Error($"{SalvageRollerManager.RefundPrefix} {host.CharacterName} (0x{host.CharacterId:X8}), account {host.AccountId}, batch {batch?.Id.ToString() ?? "-"}, item 0x{batch?.ItemGuid ?? 0:X8}: {what} Detail: {json}");

            try
            {
                var row = new JsonObject
                {
                    ["what"] = what,
                    ["exception"] = exception?.ToString(),
                    ["detail"] = detail == null ? null : JsonNode.Parse(json),
                    ["moves"] = Moves("as described in 'what'", "nothing moved by this record; left for staff"),
                };

                Write(host, SalvageRollerAuditEvent.Error, batch, batch?.ItemGuid, row);
            }
            catch (Exception ex)
            {
                SalvageRollerManager.Log.Error($"{SalvageRollerManager.RefundPrefix} the Error audit row for \"{what}\" threw: {ex.Message}");
            }
        }

        // ---------------- detail parts ----------------

        internal static JsonObject BatchJson(CharacterSalvageBatch batch)
        {
            if (batch == null)
                return null;

            return new JsonObject
            {
                ["id"] = batch.Id,
                ["characterId"] = batch.CharacterId,
                ["accountId"] = batch.AccountId,
                ["itemGuid"] = batch.ItemGuid,
                ["itemGuidHex"] = $"0x{batch.ItemGuid:X8}",
                ["itemWcid"] = batch.ItemWcid,
                ["action"] = ((SalvageRollerAction)batch.Action).ToString(),
                ["state"] = ((SalvageBatchState)batch.State).ToString(),
                ["chosenSeq"] = batch.ChosenSeq,
                ["rollCount"] = batch.RollCount,
                ["createdAtUtc"] = batch.CreatedAt.ToString("o"),
                ["items"] = new JsonArray((batch.Items ?? new List<CharacterSalvageBatchItem>())
                    .OrderBy(i => i.Guid)
                    .Select(i => (JsonNode)new JsonObject { ["guid"] = i.Guid, ["hex"] = $"0x{i.Guid:X8}", ["role"] = ((SalvageBatchItemRole)i.Role).ToString() })
                    .ToArray()),
            };
        }

        /// <summary>
        /// One item, complete: guid, wcid, name, material, charges, where it is, and its whole biota
        /// (<see cref="SalvageRollerAuditSnapshot"/>), which alone is enough to recreate it.
        ///
        /// <paramref name="asGuid"/> records a scratch copy under the real item's guid: the take's post-take state is
        /// built on a copy with no guid of its own, and the record must name the item it describes.
        /// </summary>
        internal static JsonObject ItemJson(WorldObject item, string where = null, uint? asGuid = null)
        {
            if (item == null)
                return null;

            var guid = asGuid ?? item.Guid.Full;

            var json = new JsonObject
            {
                ["guid"] = guid,
                ["hex"] = $"0x{guid:X8}",
                ["wcid"] = item.WeenieClassId,
                ["name"] = item.Name,
                ["material"] = item.MaterialType?.ToString(),
                ["charges"] = item.Structure,
                ["maxCharges"] = item.MaxStructure,
                ["batchApplied"] = item.GetProperty(PropertyInt64.SalvageBatchApplied),
                ["container"] = item.ContainerId,
                ["wielder"] = item.WielderId,
            };

            if (where != null)
                json["where"] = where;

            var snapshot = SnapshotNode(item);

            if (asGuid.HasValue && snapshot is JsonObject full && full["biota"] is JsonObject biota)
            {
                full["src"] = asGuid.Value;
                biota["Id"] = asGuid.Value;
            }

            json["snapshot"] = snapshot;

            return json;
        }

        internal static JsonArray ItemsJson(IEnumerable<WorldObject> items, string where = null)
        {
            return new JsonArray((items ?? Enumerable.Empty<WorldObject>()).Where(i => i != null).Select(i => (JsonNode)ItemJson(i, where)).ToArray());
        }

        /// <summary>The item's whole biota as a JSON node (<see cref="SalvageRollerAuditSnapshot"/>). Never capped: an audit row is MEDIUMTEXT.</summary>
        internal static JsonNode SnapshotNode(WorldObject item)
        {
            try
            {
                return SalvageRollerAuditSnapshot.SerializeToNode(item);
            }
            catch (Exception ex)
            {
                return new JsonObject { ["captureFailed"] = ex.Message };
            }
        }

        internal static JsonObject CandidateJson(SalvageRollerCandidate candidate)
        {
            if (candidate == null)
                return null;

            return JsonSerializer.SerializeToNode(candidate, SalvageRollerPayload.Options)?.AsObject();
        }

        internal static JsonObject Moves(string before, string after)
        {
            return new JsonObject { ["before"] = before, ["after"] = after };
        }
    }
}
