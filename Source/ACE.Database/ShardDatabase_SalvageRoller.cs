using System;
using System.Linq;

using Microsoft.EntityFrameworkCore;

using ACE.Common.Extensions;
using ACE.Database.Models.Shard;

namespace ACE.Database
{
    /// <summary>
    /// Salvage Roller - the character_salvage_batch, character_salvage_batch_item and salvage_roller_audit
    /// tables (Docs/SalvageRoller/DESIGN.md 5.2, 5.9).
    ///
    /// Row operations, not biota operations, so like ShardDatabase_AccountVault.cs they bypass
    /// SerializedShardDatabase's worker queue: the caller (the Salvage Roller, running on the account
    /// store's mutation queue) serializes them itself.
    ///
    /// NOTHING in this file may open a user-initiated EF transaction: ShardDbContext enables
    /// EnableRetryOnFailure, and MySqlRetryingExecutionStrategy refuses one outright. Every write here is
    /// ONE SaveChanges, ONE ExecuteUpdate or ONE ExecuteDelete, each its own implicit transaction. Where a
    /// write must cover several rows atomically (a batch plus its items) it is built as one SaveChanges.
    ///
    /// READ FAILURE CONTRACT, as for the vault: a read returns false when it FAILED, and true with a null
    /// result when there is genuinely nothing. A failed read must never look like "no batch", because the
    /// caller would then let the player open a second batch over items still in escrow.
    /// </summary>
    public partial class ShardDatabase
    {
        /// <summary>
        /// The character's open batch with its item rows, or null when it has none. False when the read
        /// failed - see the read failure contract on the class remarks.
        /// </summary>
        public bool TryGetSalvageBatchForCharacter(uint characterId, out CharacterSalvageBatch batch)
        {
            batch = null;

            try
            {
                using (var context = new ShardDbContext())
                {
                    context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                    batch = context.CharacterSalvageBatch
                        .Include(b => b.Items)
                        .FirstOrDefault(b => b.CharacterId == characterId);
                }

                return true;
            }
            catch (Exception ex)
            {
                log.Error($"[SALVAGE ROLLER] TryGetSalvageBatchForCharacter failed for character 0x{characterId:X8}: {ex.GetFullMessage()}");
                batch = null;
                return false;
            }
        }

        /// <summary>
        /// Inserts a new batch (state Escrowing) and every one of its <see cref="CharacterSalvageBatch.Items"/>
        /// in ONE SaveChanges, so either all of them land or none does. False on any failure, including the
        /// UNIQUE character key refusing a second batch for the same character.
        ///
        /// CreatedAt is stamped with DateTime.UtcNow when left at default, for the reason AddAccountVault
        /// gives: EF omits a store-default column from the INSERT while it holds the CLR default, and the
        /// row would then take the database server's LOCAL time.
        /// </summary>
        public bool AddSalvageBatch(CharacterSalvageBatch batch)
        {
            if (batch == null)
            {
                log.Error("[SALVAGE ROLLER] AddSalvageBatch called with a null batch.");
                return false;
            }

            if (batch.CreatedAt == default)
                batch.CreatedAt = DateTime.UtcNow;

            try
            {
                using (var context = new ShardDbContext())
                {
                    context.CharacterSalvageBatch.Add(batch);
                    context.SaveChanges();
                }

                return true;
            }
            catch (Exception ex)
            {
                log.Error($"[SALVAGE ROLLER] AddSalvageBatch failed for character 0x{batch.CharacterId:X8}, item 0x{batch.ItemGuid:X8}: {ex.GetFullMessage()}");
                return false;
            }
        }

        /// <summary>
        /// Commits a rolled batch: Escrowing -> Pending, writing the payload in the same statement. Guarded
        /// on the current state, so it changes exactly one row or none; false when no Escrowing row with
        /// that id exists (or the write failed).
        /// </summary>
        public bool TryCommitSalvageBatchPending(ulong batchId, string payload)
        {
            if (string.IsNullOrEmpty(payload))
            {
                log.Error($"[SALVAGE ROLLER] TryCommitSalvageBatchPending called with no payload for batch {batchId}.");
                return false;
            }

            return TryMoveSalvageBatchState(batchId, SalvageBatchState.Escrowing, SalvageBatchState.Pending, payload, null);
        }

        /// <summary>
        /// Commits the player's pick: Pending -> Taking with chosen_Seq, in one guarded statement. False when
        /// no Pending row with that id exists (or the write failed).
        /// </summary>
        public bool TrySetSalvageBatchTaking(ulong batchId, int chosenSeq)
        {
            return TryMoveSalvageBatchState(batchId, SalvageBatchState.Pending, SalvageBatchState.Taking, null, chosenSeq);
        }

        private bool TryMoveSalvageBatchState(ulong batchId, SalvageBatchState from, SalvageBatchState to, string payload, int? chosenSeq)
        {
            var fromValue = (byte)from;
            var toValue = (byte)to;

            try
            {
                using (var context = new ShardDbContext())
                {
                    var query = context.CharacterSalvageBatch.Where(b => b.Id == batchId && b.State == fromValue);

                    int changed;

                    if (payload != null)
                        changed = query.ExecuteUpdate(s => s.SetProperty(b => b.State, toValue).SetProperty(b => b.Payload, payload));
                    else if (chosenSeq != null)
                        changed = query.ExecuteUpdate(s => s.SetProperty(b => b.State, toValue).SetProperty(b => b.ChosenSeq, chosenSeq));
                    else
                        changed = query.ExecuteUpdate(s => s.SetProperty(b => b.State, toValue));

                    if (changed != 1)
                        log.Error($"[SALVAGE ROLLER] batch {batchId}: a {from} -> {to} move changed {changed} rows, expected 1.");

                    return changed == 1;
                }
            }
            catch (Exception ex)
            {
                log.Error($"[SALVAGE ROLLER] batch {batchId}: the {from} -> {to} move failed: {ex.GetFullMessage()}");
                return false;
            }
        }

        /// <summary>
        /// Deletes one item row - a Hammer that has been consumed. True when the row is gone afterwards,
        /// including when it was already gone, because a reconcile repeating this step must be safe.
        /// </summary>
        public bool DeleteSalvageBatchItem(uint guid)
        {
            try
            {
                using (var context = new ShardDbContext())
                {
                    context.CharacterSalvageBatchItem.Where(i => i.Guid == guid).ExecuteDelete();
                }

                return true;
            }
            catch (Exception ex)
            {
                log.Error($"[SALVAGE ROLLER] DeleteSalvageBatchItem failed for 0x{guid:X8}: {ex.GetFullMessage()}");
                return false;
            }
        }

        /// <summary>
        /// Deletes a batch and every item row it still has, in ONE SaveChanges. True when they are gone
        /// afterwards, including when the batch was already gone (a repeated reconcile step).
        ///
        /// CALLER CONTRACT: this drops EVERY purge exemption the batch held (the orphan purge keeps a
        /// parentless biota only while a batch item row names it). Call it only AFTER the delivery save of
        /// the target has landed, so the item already has its new parent recorded; deleting first opens a
        /// window in which a crash plus an orphan purge destroys the item.
        /// </summary>
        public bool DeleteSalvageBatch(ulong batchId)
        {
            try
            {
                using (var context = new ShardDbContext())
                {
                    var items = context.CharacterSalvageBatchItem.Where(i => i.BatchId == batchId).ToList();
                    var batch = context.CharacterSalvageBatch.FirstOrDefault(b => b.Id == batchId);

                    context.CharacterSalvageBatchItem.RemoveRange(items);

                    if (batch != null)
                        context.CharacterSalvageBatch.Remove(batch);

                    context.SaveChanges();
                }

                return true;
            }
            catch (Exception ex)
            {
                log.Error($"[SALVAGE ROLLER] DeleteSalvageBatch failed for batch {batchId}: {ex.GetFullMessage()}");
                return false;
            }
        }

        /// <summary>
        /// Appends one audit row in its own SaveChanges. False on failure: per DESIGN 5.9 the caller must
        /// then refuse the step the row was about to record, so no value moves unrecorded.
        /// </summary>
        public bool AddSalvageRollerAudit(SalvageRollerAudit row)
        {
            if (row == null)
            {
                log.Error("[SALVAGE ROLLER] AddSalvageRollerAudit called with a null row.");
                return false;
            }

            if (row.CreatedAt == default)
                row.CreatedAt = DateTime.UtcNow;

            if (string.IsNullOrEmpty(row.CharacterName))
                row.CharacterName = "<unknown>";

            try
            {
                using (var context = new ShardDbContext())
                {
                    context.SalvageRollerAudit.Add(row);
                    context.SaveChanges();
                }

                return true;
            }
            catch (Exception ex)
            {
                log.Error($"[SALVAGE ROLLER REFUND] AddSalvageRollerAudit failed for character 0x{row.CharacterId:X8}, batch {row.BatchId}, event {row.Event}, item 0x{row.ItemGuid ?? 0:X8}: {ex.GetFullMessage()}. Detail follows so the record is not lost: {row.Detail}");
                return false;
            }
        }

        /// <summary>
        /// A biota with every property collection populated, read through the caller's context. For the
        /// character-purge hook, which has to snapshot an escrowed item into its audit row inside the same
        /// context that deletes it. The returned entity is TRACKED by <paramref name="context"/>.
        ///
        /// Calls the non-virtual GetBiotaCore deliberately, never the virtual GetBiota: the caching subclass
        /// would otherwise hand this caller's context to its cache (see CLAUDE.md, SerializedShardDatabase).
        /// </summary>
        internal Biota GetBiotaWithCollections(ShardDbContext context, uint id)
        {
            return GetBiotaCore(context, id);
        }

        /// <summary>
        /// A biota with every property collection populated, read through a FRESH context and never through
        /// the biota cache, or null when there is no such row. For the Salvage Roller's reconcile, which must
        /// see what the shard holds now, not a cached copy that may predate the last save. Throws when the
        /// read fails, so the caller can tell "no row" from "could not read".
        ///
        /// GetBiotaCore, never the virtual GetBiota: the caching subclass would otherwise park this context
        /// in its cache and hand it back disposed (see CLAUDE.md, SerializedShardDatabase).
        /// </summary>
        public Biota GetSalvageRollerBiotaUncached(uint id)
        {
            using (var context = new ShardDbContext())
                return GetBiotaCore(context, id);
        }

        /// <summary>
        /// The detail of the most recent audit row of <paramref name="auditEvent"/> for this batch, or null
        /// when there is none. False when the read failed. The take reads its own Taken row back from here
        /// when a delivery is finished later, because the item's pre-take state lives only in that row.
        /// </summary>
        public bool TryGetLatestSalvageRollerAuditDetail(ulong batchId, SalvageRollerAuditEvent auditEvent, out string detail)
        {
            detail = null;

            var ev = (byte)auditEvent;

            try
            {
                using (var context = new ShardDbContext())
                {
                    detail = context.SalvageRollerAudit
                        .AsNoTracking()
                        .Where(a => a.BatchId == batchId && a.Event == ev)
                        .OrderByDescending(a => a.Id)
                        .Select(a => a.Detail)
                        .FirstOrDefault();
                }

                return true;
            }
            catch (Exception ex)
            {
                log.Error($"[SALVAGE ROLLER] TryGetLatestSalvageRollerAuditDetail failed for batch {batchId}, event {auditEvent}: {ex.GetFullMessage()}");
                detail = null;
                return false;
            }
        }
    }
}
