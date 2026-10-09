using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.EntityFrameworkCore;

using ACE.Common.Extensions;
using ACE.Database.Models.Shard;

namespace ACE.Database
{
    /// <summary>
    /// PvP Template Facets: the DAO over `pvp_template`, `pvp_template_history` and the two template stamp
    /// columns on `pvp_match_participant` (Database/Updates/Shard/2026-10-03-00-Add-Pvp-Templates.sql,
    /// Docs/Pvp/TEMPLATES.md "Data model").
    ///
    /// THE BOUNDARY. Every public member takes or returns the plain records in PvpTemplateRecords.cs, the
    /// same split ShardDatabase_PvpArena.cs keeps.
    ///
    /// BOOTS WITHOUT THE MIGRATION. A missing table (MySQL 1146) is reported as
    /// <see cref="PvpTemplateStoreStatus.TablesMissing"/> and logged as a warning, never thrown, so template
    /// features refuse cleanly on a shard the migration has not reached. Nothing here ever throws.
    ///
    /// TRANSACTIONS. A snapshot writes the template row and its history row in ONE SaveChanges, its own
    /// implicit transaction. The one multi-statement write (the participant stamps) goes through
    /// context.Database.CreateExecutionStrategy().Execute(...) with an idempotent delegate, because
    /// ShardDbContext's EnableRetryOnFailure refuses a bare BeginTransaction (see the repo CLAUDE.md).
    ///
    /// NOT BIOTA OPERATIONS: nothing here is virtual or mirrored on ShardDatabaseWithCaching.
    /// </summary>
    public partial class ShardDatabase
    {
        /// <summary>
        /// Every template row. <paramref name="status"/> is Ok (a list, possibly empty), TablesMissing (an
        /// empty list) or Failed (null - the caller must not read that as "no templates exist").
        /// </summary>
        public List<PvpTemplateRecord> GetAllPvpTemplates(out PvpTemplateStoreStatus status)
        {
            try
            {
                using (var context = new ShardDbContext())
                {
                    context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                    var rows = context.PvpTemplate.ToList().Select(ToRecord).ToList();

                    status = PvpTemplateStoreStatus.Ok;
                    return rows;
                }
            }
            catch (Exception ex) when (IsMissingTableError(ex))
            {
                log.Warn($"[PVPTEMPLATE] GetAllPvpTemplates: the pvp_template table does not exist - no templates are available. Expected until Database/Updates/Shard/2026-10-03-00-Add-Pvp-Templates.sql is applied. {ex.GetFullMessage()}");
                status = PvpTemplateStoreStatus.TablesMissing;
                return new List<PvpTemplateRecord>();
            }
            catch (Exception ex)
            {
                log.Error($"[PVPTEMPLATE] GetAllPvpTemplates failed - templates are UNAVAILABLE, not empty: {ex.GetFullMessage()}");
                status = PvpTemplateStoreStatus.Failed;
                return null;
            }
        }

        /// <summary>One template row by key, or null with status NotFound / TablesMissing / Failed.</summary>
        public PvpTemplateRecord GetPvpTemplate(string templateKey, out PvpTemplateStoreStatus status)
        {
            if (string.IsNullOrEmpty(templateKey))
            {
                status = PvpTemplateStoreStatus.NotFound;
                return null;
            }

            try
            {
                using (var context = new ShardDbContext())
                {
                    context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                    var row = context.PvpTemplate.FirstOrDefault(t => t.TemplateKey == templateKey);

                    status = row == null ? PvpTemplateStoreStatus.NotFound : PvpTemplateStoreStatus.Ok;
                    return row == null ? null : ToRecord(row);
                }
            }
            catch (Exception ex) when (IsMissingTableError(ex))
            {
                log.Warn($"[PVPTEMPLATE] GetPvpTemplate({templateKey}): the pvp_template table does not exist. {ex.GetFullMessage()}");
                status = PvpTemplateStoreStatus.TablesMissing;
                return null;
            }
            catch (Exception ex)
            {
                log.Error($"[PVPTEMPLATE] GetPvpTemplate({templateKey}) failed: {ex.GetFullMessage()}");
                status = PvpTemplateStoreStatus.Failed;
                return null;
            }
        }

        /// <summary>One history row by (key, version), or null with status NotFound / TablesMissing / Failed.</summary>
        public PvpTemplateRecord GetPvpTemplateHistory(string templateKey, uint version, out PvpTemplateStoreStatus status)
        {
            try
            {
                using (var context = new ShardDbContext())
                {
                    context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                    var row = context.PvpTemplateHistory.FirstOrDefault(t => t.TemplateKey == templateKey && t.Version == version);

                    if (row == null)
                    {
                        status = PvpTemplateStoreStatus.NotFound;
                        return null;
                    }

                    status = PvpTemplateStoreStatus.Ok;

                    return new PvpTemplateRecord
                    {
                        TemplateKey = row.TemplateKey,
                        Version = row.Version,
                        DefinitionJson = row.DefinitionJson,
                        SnapshotAt = DateTime.SpecifyKind(row.SnapshotAt, DateTimeKind.Utc),
                        SnapshotBy = row.SnapshotBy,
                    };
                }
            }
            catch (Exception ex) when (IsMissingTableError(ex))
            {
                log.Warn($"[PVPTEMPLATE] GetPvpTemplateHistory({templateKey}, {version}): the pvp_template_history table does not exist. {ex.GetFullMessage()}");
                status = PvpTemplateStoreStatus.TablesMissing;
                return null;
            }
            catch (Exception ex)
            {
                log.Error($"[PVPTEMPLATE] GetPvpTemplateHistory({templateKey}, {version}) failed: {ex.GetFullMessage()}");
                status = PvpTemplateStoreStatus.Failed;
                return null;
            }
        }

        /// <summary>
        /// Writes one snapshot: upserts the `pvp_template` row for <paramref name="snapshot"/>.TemplateKey with
        /// version = previous version + 1 (1 for a new key), and appends the matching `pvp_template_history`
        /// row, both in ONE SaveChanges. An existing row keeps its Enabled and Modes (a re-snapshot never
        /// changes which modes offer a template or whether it is switched on); a NEW row takes them from
        /// <paramref name="snapshot"/>. <paramref name="snapshot"/>.Version is ignored; the assigned version is
        /// reported through <paramref name="newVersion"/>.
        ///
        /// Ambiguous on a duplicate key (see <see cref="PvpTemplateStoreStatus.Ambiguous"/>). Never throws.
        /// </summary>
        public PvpTemplateStoreStatus SavePvpTemplateSnapshot(PvpTemplateRecord snapshot, out uint newVersion)
        {
            newVersion = 0;

            var problem = ValidatePvpTemplateSnapshot(snapshot);

            if (problem != null)
            {
                log.Error($"[PVPTEMPLATE] SavePvpTemplateSnapshot refused: {problem}");
                return PvpTemplateStoreStatus.Failed;
            }

            try
            {
                using (var context = new ShardDbContext())
                {
                    var at = snapshot.SnapshotAt == default ? DateTime.UtcNow : ToUtc(snapshot.SnapshotAt);

                    // Tracked on purpose: an existing row is modified in place by the SaveChanges below.
                    var row = context.PvpTemplate.FirstOrDefault(t => t.TemplateKey == snapshot.TemplateKey);

                    if (row == null)
                    {
                        row = new PvpTemplate
                        {
                            TemplateKey = snapshot.TemplateKey,
                            Version = 1,
                            Enabled = snapshot.Enabled,
                            Modes = snapshot.Modes ?? string.Empty,
                        };

                        context.PvpTemplate.Add(row);
                    }
                    else
                        row.Version++;

                    row.DisplayName = snapshot.DisplayName ?? snapshot.TemplateKey;
                    row.SourceCharacterId = snapshot.SourceCharacterId;
                    row.SourceCharacterName = snapshot.SourceCharacterName ?? string.Empty;
                    row.DefinitionJson = snapshot.DefinitionJson;
                    row.SnapshotAt = at;
                    row.SnapshotBy = snapshot.SnapshotBy ?? string.Empty;

                    context.PvpTemplateHistory.Add(new PvpTemplateHistory
                    {
                        TemplateKey = row.TemplateKey,
                        Version = row.Version,
                        DefinitionJson = row.DefinitionJson,
                        SnapshotAt = at,
                        SnapshotBy = row.SnapshotBy,
                    });

                    context.SaveChanges();

                    newVersion = row.Version;
                    return PvpTemplateStoreStatus.Ok;
                }
            }
            catch (Exception ex) when (IsMissingTableError(ex))
            {
                log.Warn($"[PVPTEMPLATE] SavePvpTemplateSnapshot({snapshot.TemplateKey}): the template tables do not exist - nothing was written. {ex.GetFullMessage()}");
                return PvpTemplateStoreStatus.TablesMissing;
            }
            catch (Exception ex) when (IsDuplicateKeyError(ex))
            {
                log.Error($"[PVPTEMPLATE] SavePvpTemplateSnapshot({snapshot.TemplateKey}) hit a duplicate key - OUTCOME AMBIGUOUS, the snapshot may already be saved by an earlier committed attempt: {ex.GetFullMessage()}");
                return PvpTemplateStoreStatus.Ambiguous;
            }
            catch (Exception ex)
            {
                log.Error($"[PVPTEMPLATE] SavePvpTemplateSnapshot({snapshot.TemplateKey}) failed - nothing was written: {ex.GetFullMessage()}");
                return PvpTemplateStoreStatus.Failed;
            }
        }

        /// <summary>Sets the admin switch on an existing key. NotFound when the key does not exist.</summary>
        public PvpTemplateStoreStatus SetPvpTemplateEnabled(string templateKey, bool enabled)
            => UpdatePvpTemplate(templateKey, row => row.Enabled = enabled, $"enabled={enabled}");

        /// <summary>
        /// Sets the comma-separated mode list on an existing key. The caller validates the mode keys; this layer
        /// only enforces the column width. NotFound when the key does not exist.
        /// </summary>
        public PvpTemplateStoreStatus SetPvpTemplateModes(string templateKey, string modes)
        {
            modes ??= string.Empty;

            if (modes.Length > 64)
            {
                log.Error($"[PVPTEMPLATE] SetPvpTemplateModes({templateKey}) refused: the mode list is {modes.Length} characters, the column holds 64.");
                return PvpTemplateStoreStatus.Failed;
            }

            return UpdatePvpTemplate(templateKey, row => row.Modes = modes, $"modes={modes}");
        }

        /// <summary>
        /// Sets the display name on an existing key (shown beside player names on results, the announcement and /top).
        /// The caller sanitizes it; this layer only enforces the column (1 to 64 characters). NotFound when the key
        /// does not exist.
        /// </summary>
        public PvpTemplateStoreStatus SetPvpTemplateDisplayName(string templateKey, string displayName)
        {
            if (string.IsNullOrWhiteSpace(displayName) || displayName.Length > 64)
            {
                log.Error($"[PVPTEMPLATE] SetPvpTemplateDisplayName({templateKey}) refused: the name must be 1 to 64 characters (got {displayName?.Length ?? 0}).");
                return PvpTemplateStoreStatus.Failed;
            }

            return UpdatePvpTemplate(templateKey, row => row.DisplayName = displayName, $"display name={displayName}");
        }

        private PvpTemplateStoreStatus UpdatePvpTemplate(string templateKey, Action<PvpTemplate> change, string what)
        {
            if (string.IsNullOrEmpty(templateKey))
                return PvpTemplateStoreStatus.NotFound;

            try
            {
                using (var context = new ShardDbContext())
                {
                    var row = context.PvpTemplate.FirstOrDefault(t => t.TemplateKey == templateKey);

                    if (row == null)
                        return PvpTemplateStoreStatus.NotFound;

                    change(row);

                    context.SaveChanges();

                    return PvpTemplateStoreStatus.Ok;
                }
            }
            catch (Exception ex) when (IsMissingTableError(ex))
            {
                log.Warn($"[PVPTEMPLATE] update {templateKey} ({what}): the pvp_template table does not exist - nothing was written. {ex.GetFullMessage()}");
                return PvpTemplateStoreStatus.TablesMissing;
            }
            catch (Exception ex)
            {
                log.Error($"[PVPTEMPLATE] update {templateKey} ({what}) failed - nothing was written: {ex.GetFullMessage()}");
                return PvpTemplateStoreStatus.Failed;
            }
        }

        /// <summary>
        /// Stamps each participant's template onto an already-saved match's participant rows.
        ///
        /// WHY A SEPARATE WRITE, not two more mapped columns on PvpMatchParticipant: a mapped column is in every
        /// participant INSERT, so a shard whose template migration has not run would fail to save EVERY match,
        /// templated or not. This UPDATE instead fails alone, and a missing column (MySQL 1054) is reported as
        /// TablesMissing and logged as a warning while the match itself stays saved. The price is that the stamp
        /// is not atomic with the match row: a crash between the two leaves the stamp NULL, which reads as "no
        /// template recorded", never as a wrong one.
        ///
        /// All rows go in one explicit transaction, opened through the execution strategy (a bare
        /// BeginTransaction is refused under EnableRetryOnFailure). The delegate is idempotent - every UPDATE
        /// writes absolute values keyed on (match_Id, character_Id) - so a strategy retry is harmless.
        /// Returns Ok, TablesMissing or Failed. Never throws.
        /// </summary>
        public PvpTemplateStoreStatus SetPvpMatchParticipantTemplates(uint matchId, IReadOnlyList<PvpMatchParticipantTemplateRecord> stamps)
        {
            if (stamps == null || stamps.Count == 0)
                return PvpTemplateStoreStatus.Ok;

            try
            {
                using (var context = new ShardDbContext())
                {
                    var strategy = context.Database.CreateExecutionStrategy();

                    strategy.Execute(() =>
                    {
                        using (var transaction = context.Database.BeginTransaction())
                        {
                            foreach (var stamp in stamps)
                            {
                                if (stamp == null)
                                    continue;

                                context.Database.ExecuteSqlInterpolated(
                                    $"UPDATE `pvp_match_participant` SET `template_Key` = {stamp.TemplateKey}, `template_Version` = {stamp.TemplateVersion} WHERE `match_Id` = {matchId} AND `character_Id` = {stamp.CharacterId}");
                            }

                            transaction.Commit();
                        }
                    });

                    return PvpTemplateStoreStatus.Ok;
                }
            }
            catch (Exception ex) when (IsMissingTableError(ex) || IsUnknownColumnError(ex))
            {
                log.Warn($"[PVPTEMPLATE] SetPvpMatchParticipantTemplates(match {matchId}): the participant template columns do not exist - the match is saved without its template stamps. Expected until Database/Updates/Shard/2026-10-03-00-Add-Pvp-Templates.sql is applied. {ex.GetFullMessage()}");
                return PvpTemplateStoreStatus.TablesMissing;
            }
            catch (Exception ex)
            {
                log.Error($"[PVPTEMPLATE] SetPvpMatchParticipantTemplates(match {matchId}) failed - the match is saved without its template stamps: {ex.GetFullMessage()}");
                return PvpTemplateStoreStatus.Failed;
            }
        }

        /// <summary>
        /// For every (character, ladder) pair: the template stamped on that character's most recent stamped match on
        /// that ladder (the highest `match_Id` whose `template_Key` is set). The boot read behind /top's template column.
        ///
        /// Raw SQL, like the stamp write above, because the two stamp columns are deliberately not mapped on
        /// PvpMatchParticipant (see SetPvpMatchParticipantTemplates). A missing table (1146) or missing column (1054)
        /// is TablesMissing with an empty list; any other failure is Failed with null. Never throws.
        /// </summary>
        public List<PvpLatestParticipantTemplateRecord> GetLatestPvpParticipantTemplates(out PvpTemplateStoreStatus status)
        {
            const string sql =
                "SELECT p.`character_Id`, m.`ladder`, p.`template_Key`, p.`template_Version` " +
                "FROM `pvp_match_participant` p " +
                "JOIN `pvp_match` m ON m.`id` = p.`match_Id` " +
                "JOIN (SELECT p2.`character_Id` AS cid, m2.`ladder` AS lad, MAX(p2.`match_Id`) AS mid " +
                "      FROM `pvp_match_participant` p2 JOIN `pvp_match` m2 ON m2.`id` = p2.`match_Id` " +
                "      WHERE p2.`template_Key` IS NOT NULL GROUP BY p2.`character_Id`, m2.`ladder`) latest " +
                "  ON latest.cid = p.`character_Id` AND latest.lad = m.`ladder` AND latest.mid = p.`match_Id`";

            try
            {
                using (var context = new ShardDbContext())
                {
                    var connection = context.Database.GetDbConnection();
                    connection.Open();

                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = sql;

                        using (var reader = command.ExecuteReader())
                        {
                            var rows = new List<PvpLatestParticipantTemplateRecord>();

                            while (reader.Read())
                            {
                                rows.Add(new PvpLatestParticipantTemplateRecord
                                {
                                    CharacterId = Convert.ToUInt32(reader.GetValue(0)),
                                    Ladder = reader.GetValue(1) as string ?? Convert.ToString(reader.GetValue(1)),
                                    TemplateKey = reader.GetValue(2) as string ?? Convert.ToString(reader.GetValue(2)),
                                    TemplateVersion = reader.IsDBNull(3) ? 0u : Convert.ToUInt32(reader.GetValue(3)),
                                });
                            }

                            status = PvpTemplateStoreStatus.Ok;
                            return rows;
                        }
                    }
                }
            }
            catch (Exception ex) when (IsMissingTableError(ex) || IsUnknownColumnError(ex))
            {
                log.Warn($"[PVPTEMPLATE] GetLatestPvpParticipantTemplates: the arena tables or the participant template columns do not exist - /top shows no templates. Expected until Database/Updates/Shard/2026-10-03-00-Add-Pvp-Templates.sql is applied. {ex.GetFullMessage()}");
                status = PvpTemplateStoreStatus.TablesMissing;
                return new List<PvpLatestParticipantTemplateRecord>();
            }
            catch (Exception ex)
            {
                log.Error($"[PVPTEMPLATE] GetLatestPvpParticipantTemplates failed - /top shows no templates until the next match stamps one: {ex.GetFullMessage()}");
                status = PvpTemplateStoreStatus.Failed;
                return null;
            }
        }

        /// <summary>Null when the snapshot is acceptable, else a one-line reason. Pure, so it is unit tested.</summary>
        internal static string ValidatePvpTemplateSnapshot(PvpTemplateRecord snapshot)
        {
            if (snapshot == null)
                return "the snapshot is null";

            if (string.IsNullOrEmpty(snapshot.TemplateKey))
                return "the snapshot has no template key";

            if (snapshot.TemplateKey.Length > 32)
                return $"the template key '{snapshot.TemplateKey}' is longer than 32 characters";

            if (snapshot.TemplateKey.Any(c => c > 0x7F))
                return $"the template key '{snapshot.TemplateKey}' is not ASCII";

            if (string.IsNullOrEmpty(snapshot.DefinitionJson))
                return $"the snapshot for '{snapshot.TemplateKey}' has no definition";

            if ((snapshot.DisplayName?.Length ?? 0) > 64)
                return $"the display name for '{snapshot.TemplateKey}' is longer than 64 characters";

            if ((snapshot.Modes?.Length ?? 0) > 64)
                return $"the mode list for '{snapshot.TemplateKey}' is longer than 64 characters";

            return null;
        }

        /// <summary>True when any exception in the chain is MySQL ER_BAD_FIELD_ERROR (1054, unknown column).</summary>
        internal static bool IsUnknownColumnError(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (e is MySqlConnector.MySqlException mysql && mysql.ErrorCode == MySqlConnector.MySqlErrorCode.BadFieldError)
                    return true;
            }

            return false;
        }

        internal static PvpTemplateRecord ToRecord(PvpTemplate row) => new PvpTemplateRecord
        {
            TemplateKey = row.TemplateKey,
            DisplayName = row.DisplayName,
            SourceCharacterId = row.SourceCharacterId,
            SourceCharacterName = row.SourceCharacterName,
            Version = row.Version,
            Enabled = row.Enabled,
            Modes = row.Modes ?? string.Empty,
            DefinitionJson = row.DefinitionJson,
            SnapshotAt = DateTime.SpecifyKind(row.SnapshotAt, DateTimeKind.Utc),
            SnapshotBy = row.SnapshotBy,
        };
    }
}
