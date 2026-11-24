using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.EntityFrameworkCore;

using MySqlConnector;

using ACE.Common.Extensions;
using ACE.Database.Models.Shard;

namespace ACE.Database
{
    /// <summary>What one allegiance_gauntlet_claim INSERT did.</summary>
    public enum GauntletClaimInsertResult
    {
        /// <summary>The row was written by this call: the account's crate for that UTC day is now taken.</summary>
        Inserted,

        /// <summary>
        /// MySQL refused the row with 1062 and the row that holds (account_Id, claim_Date) carries a different
        /// claim token: another claim already took this account's crate for that UTC day.
        /// </summary>
        AlreadyClaimedToday,

        /// <summary>
        /// MySQL refused the row with 1062, but the row that holds (account_Id, claim_Date) carries THIS attempt's
        /// claim token: an earlier try of this same attempt committed it and its acknowledgement was lost, so the
        /// retry collided with its own row. The claim succeeded; treat it exactly like <see cref="Inserted"/>.
        /// </summary>
        OwnRowOnRetry,

        /// <summary>Any other failure, or invalid input. Whether anything committed is unknown.</summary>
        Failed
    }

    /// <summary>
    /// Allegiance Gauntlet: the DAO over `allegiance_gauntlet_record`, `allegiance_gauntlet_run`,
    /// `allegiance_gauntlet_wave` and `allegiance_gauntlet_claim`
    /// (Database/Updates/Shard/2026-11-21-02-Add-Allegiance-Gauntlet.sql). The in-memory GauntletManager is the only
    /// caller. It reads the records and closes stale runs once at startup, synchronously through
    /// DatabaseManager.Shard.BaseDatabase before the world ticks, and from then on writes through
    /// SerializedShardDatabase's queue.
    ///
    /// ONE CONTEXT PER CALL. Every method opens its own ShardDbContext, LOGS AND SWALLOWS, and never throws.
    ///
    /// ABSOLUTE VALUES, NEVER DELTAS. The manager owns the running totals in memory and hands this layer the values
    /// to store. A record is an INSERT ... ON DUPLICATE KEY UPDATE col = VALUES(col) keyed by avatar_Id, a wave the
    /// same keyed by (run_Id, wave), and a run's progress an UPDATE by run_Id. A statement re-run by the retrying
    /// execution strategy, or a whole write resubmitted after a lost acknowledgement, writes the same values again
    /// instead of adding them twice. CloseOpenGauntletRuns only touches rows whose ended_At is still NULL, so a
    /// re-run matches nothing.
    ///
    /// NO EXPLICIT TRANSACTION. Every write here is ONE statement (an EF SaveChanges, or one ExecuteSqlRaw), which is
    /// its own implicit transaction and is retried whole by the strategy ShardDbContext turns on
    /// (EnableRetryOnFailure). The claim's duplicate read-back runs only after the INSERT has failed and writes
    /// nothing. A future multi-statement write must open its transaction inside
    /// context.Database.CreateExecutionStrategy().Execute(...), as SaveAvatarBatch does (see the repo CLAUDE.md).
    ///
    /// TIMESTAMPS ARE UTC, WHOLE SECONDS AND ALWAYS EXPLICIT. updated_At and claimed_At carry a CURRENT_TIMESTAMP
    /// default, which is the DATABASE SERVER'S local time, so nothing here relies on it. Every caller timestamp is
    /// converted to UTC and truncated to the second (MySQL `datetime` would otherwise ROUND the fraction, so a value
    /// read back could differ from the one held in memory). The claim day is always the UTC .Date.
    ///
    /// NOT BIOTA OPERATIONS. Nothing is mirrored on ShardDatabaseWithCaching, for the same reason the Avatar and CAP
    /// ledger tables are not. The six writes SerializedShardDatabase queues are virtual ONLY as a test seam
    /// (AllegianceGauntletQueueTests overrides them to see exactly what the queue hands this layer); nothing in
    /// production overrides them.
    /// </summary>
    public partial class ShardDatabase
    {
        /// <summary>end_Reason written by <see cref="CloseOpenGauntletRuns"/>: 2 Restart.</summary>
        public const byte GauntletEndReasonRestart = 2;

        private const string CloseOpenGauntletRunsSql =
            "UPDATE `allegiance_gauntlet_run` SET `ended_At` = @now, `end_Reason` = @reason WHERE `ended_At` IS NULL";

        /// <summary>
        /// The absolute-value record write, keyed on the avatar_Id PRIMARY KEY. An upsert, because the first record
        /// write for an Avatar is its first committed run and nothing founds the row earlier.
        /// </summary>
        private const string UpsertGauntletRecordSql =
            "INSERT INTO `allegiance_gauntlet_record` " +
            "(`avatar_Id`, `best_Centi`, `best_Participants`, `best_Run_Id`, `best_At`, `last_Run_At`, `run_Count`, `updated_At`) " +
            "VALUES (@avatarId, @bestCenti, @bestParticipants, @bestRunId, @bestAt, @lastRunAt, @runCount, @updatedAt) " +
            "ON DUPLICATE KEY UPDATE " +
            "`best_Centi` = VALUES(`best_Centi`), " +
            "`best_Participants` = VALUES(`best_Participants`), " +
            "`best_Run_Id` = VALUES(`best_Run_Id`), " +
            "`best_At` = VALUES(`best_At`), " +
            "`last_Run_At` = VALUES(`last_Run_At`), " +
            "`run_Count` = VALUES(`run_Count`), " +
            "`updated_At` = VALUES(`updated_At`)";

        /// <summary>The absolute-value wave write, keyed on the (run_Id, wave) PRIMARY KEY.</summary>
        private const string UpsertGauntletWaveSql =
            "INSERT INTO `allegiance_gauntlet_wave` " +
            "(`run_Id`, `wave`, `level`, `headcount`, `planned`, `killed`, `seconds`, `outcome`, `fraction_Centi`, `player_Deaths`, `fighter_Deaths`, `family`) " +
            "VALUES (@runId, @wave, @level, @headcount, @planned, @killed, @seconds, @outcome, @fractionCenti, @playerDeaths, @fighterDeaths, @family) " +
            "ON DUPLICATE KEY UPDATE " +
            "`level` = VALUES(`level`), " +
            "`headcount` = VALUES(`headcount`), " +
            "`planned` = VALUES(`planned`), " +
            "`killed` = VALUES(`killed`), " +
            "`seconds` = VALUES(`seconds`), " +
            "`outcome` = VALUES(`outcome`), " +
            "`fraction_Centi` = VALUES(`fraction_Centi`), " +
            "`player_Deaths` = VALUES(`player_Deaths`), " +
            "`fighter_Deaths` = VALUES(`fighter_Deaths`), " +
            "`family` = VALUES(`family`)";

        /// <summary>
        /// The absolute-value run progress write. An UPDATE by run_Id, deliberately NOT an upsert: every run is created
        /// by InsertGauntletRun, so a missing row is a caller bug. The start columns (avatar_Id, monarch_Guid, site_Id,
        /// started_At, favor_Price) are written once at insert and never touched here.
        /// </summary>
        private const string UpdateGauntletRunSql =
            "UPDATE `allegiance_gauntlet_run` SET " +
            "`active_At` = @activeAt, " +
            "`ended_At` = @endedAt, " +
            "`end_Reason` = @endReason, " +
            "`waves_Cleared` = @wavesCleared, " +
            "`score_Centi` = @scoreCenti, " +
            "`participants` = @participants, " +
            "`eligible` = @eligible, " +
            "`peak_Headcount` = @peakHeadcount, " +
            "`favor_Committed` = @favorCommitted, " +
            "`crates_Claimed` = @cratesClaimed, " +
            "`ranked` = @ranked " +
            "WHERE `run_Id` = @runId";

        /// <summary>A plain INSERT, never an upsert: the (account_Id, claim_Date) PRIMARY KEY turns a repeat into 1062.</summary>
        private const string InsertGauntletClaimSql =
            "INSERT INTO `allegiance_gauntlet_claim` (`account_Id`, `claim_Date`, `run_Id`, `character_Id`, `claim_Token`, `claimed_At`) " +
            "VALUES (@accountId, @claimDate, @runId, @characterId, @claimToken, @claimedAt)";

        private const int GauntletSiteIdMaxLength = 64;

        private const int GauntletFamilyMaxLength = 64;

        /// <summary>
        /// Every record row. Read once at startup. Returns an empty list when there are none and NULL when the read
        /// FAILED (including a missing table). A caller must not treat NULL as "no records": seeding an empty manager
        /// from a failed read would forget every cooldown and let the next run's record write overwrite a best score.
        /// </summary>
        public List<AllegianceGauntletRecord> GetAllGauntletRecords()
        {
            try
            {
                using (var context = new ShardDbContext())
                {
                    context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                    return context.AllegianceGauntletRecord.ToList();
                }
            }
            catch (Exception ex)
            {
                log.Error($"[GAUNTLET] GetAllGauntletRecords failed - records are UNAVAILABLE, not empty: {ex.GetFullMessage()}");
                return null;
            }
        }

        /// <summary>
        /// Closes every open run (ended_At NULL) as end_Reason 2 Restart, with ended_At = <paramref name="now"/> (UTC,
        /// to the second). Rows already ended keep their own ended_At and end_Reason. Run once at startup, before any
        /// run of this process exists: a run opened by this process would be closed too.
        /// </summary>
        /// <returns>true with the number of rows closed; false, logged, with closed 0 when the write FAILED.</returns>
        public bool CloseOpenGauntletRuns(DateTime now, out int closed)
        {
            closed = 0;

            var at = ToUtcSecond(now);

            try
            {
                using (var context = new ShardDbContext())
                {
                    closed = context.Database.ExecuteSqlRaw(CloseOpenGauntletRunsSql,
                        new MySqlParameter("@now", at),
                        new MySqlParameter("@reason", GauntletEndReasonRestart));
                }

                return true;
            }
            catch (Exception ex)
            {
                log.Error($"[GAUNTLET] CloseOpenGauntletRuns failed - open runs were NOT closed: {ex.GetFullMessage()}");
                closed = 0;
                return false;
            }
        }

        /// <summary>
        /// Creates one run row and returns the run_Id the database assigned. The caller's row is copied, never modified
        /// or attached: its RunId is ignored, a default StartedAt is stamped UTC now, and every timestamp is stored UTC
        /// to the second. Refused (false, nothing written) for a null row, an avatar_Id of 0, or a site_Id that is
        /// empty or longer than 64.
        ///
        /// THE ONE NON-IDEMPOTENT WRITE. The INSERT is one SaveChanges under the retrying strategy, and the table has
        /// no natural unique key to collide on, so if a commit's acknowledgement is lost the retry inserts a second row
        /// and this returns the second id. The first row is then an orphan history row: it stays open (ended_At NULL)
        /// until the next startup's CloseOpenGauntletRuns closes it as Restart. It carries no cooldown, Favor or record
        /// state (those live on the record row and in memory), so the cost is one stray history row, accepted here
        /// rather than adding a token column to the run table.
        /// </summary>
        /// <returns>true with the new id; false, logged, with runId 0.</returns>
        public virtual bool InsertGauntletRun(AllegianceGauntletRun row, out uint runId)
        {
            runId = 0;

            var problem = ValidateGauntletRun(row);

            if (problem != null)
            {
                log.Error($"[GAUNTLET] InsertGauntletRun refused, nothing written: {problem}");
                return false;
            }

            var entity = CopyGauntletRun(row);
            entity.RunId = 0;

            if (entity.StartedAt == default)
                entity.StartedAt = UtcNowToTheSecond();

            try
            {
                using (var context = new ShardDbContext())
                {
                    context.AllegianceGauntletRun.Add(entity);
                    context.SaveChanges();
                }

                runId = entity.RunId;
                return true;
            }
            catch (Exception ex)
            {
                log.Error($"[GAUNTLET] InsertGauntletRun for Avatar {row.AvatarId} at site {row.SiteId} failed: {ex.GetFullMessage()}");
                return false;
            }
        }

        /// <summary>
        /// Rewrites one run's progress columns with absolute values (<see cref="UpdateGauntletRunSql"/>). Used at commit,
        /// at the end, and as crates are claimed. The start columns are never touched.
        ///
        /// The run must exist: false unless the UPDATE reports exactly 1 row. That count is FOUND rows, not changed
        /// rows (MySqlConnector's UseAffectedRows defaults to false and ShardDbContext never sets it), so an unchanged
        /// re-run of the same snapshot still reports 1 and still returns true.
        /// </summary>
        /// <returns>true when the row was written; false, logged, when it was refused, missing, or the write failed.</returns>
        public virtual bool SaveGauntletRunEnd(AllegianceGauntletRun row)
        {
            if (row == null || row.RunId == 0)
            {
                log.Error("[GAUNTLET] SaveGauntletRunEnd refused, nothing written: a null row or run_Id 0; every run must come from InsertGauntletRun first.");
                return false;
            }

            var r = CopyGauntletRun(row);

            try
            {
                int found;

                using (var context = new ShardDbContext())
                {
                    found = context.Database.ExecuteSqlRaw(UpdateGauntletRunSql,
                        new MySqlParameter("@runId", r.RunId),
                        new MySqlParameter("@activeAt", DbValue(r.ActiveAt)),
                        new MySqlParameter("@endedAt", DbValue(r.EndedAt)),
                        new MySqlParameter("@endReason", DbValue(r.EndReason)),
                        new MySqlParameter("@wavesCleared", r.WavesCleared),
                        new MySqlParameter("@scoreCenti", r.ScoreCenti),
                        new MySqlParameter("@participants", r.Participants),
                        new MySqlParameter("@eligible", r.Eligible),
                        new MySqlParameter("@peakHeadcount", r.PeakHeadcount),
                        new MySqlParameter("@favorCommitted", r.FavorCommitted),
                        new MySqlParameter("@cratesClaimed", r.CratesClaimed),
                        new MySqlParameter("@ranked", r.Ranked));
                }

                if (found != 1)
                {
                    log.Error($"[GAUNTLET] SaveGauntletRunEnd: run_Id {r.RunId} matched {found} rows, expected 1; every run must come from InsertGauntletRun first.");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                log.Error($"[GAUNTLET] SaveGauntletRunEnd for run {r.RunId} failed: {ex.GetFullMessage()}");
                return false;
            }
        }

        /// <summary>
        /// Writes one wave row with absolute values (<see cref="UpsertGauntletWaveSql"/>): a second save of the same
        /// (run_Id, wave) updates that row in place, never adds one. Refused for a null row, run_Id 0, or a family
        /// longer than 64.
        /// </summary>
        /// <returns>true when the row was written; false, logged, when it was refused or the write failed.</returns>
        public virtual bool SaveGauntletWave(AllegianceGauntletWave row)
        {
            if (row == null || row.RunId == 0)
            {
                log.Error("[GAUNTLET] SaveGauntletWave refused, nothing written: a null row or run_Id 0.");
                return false;
            }

            if (row.Family != null && row.Family.Length > GauntletFamilyMaxLength)
            {
                log.Error($"[GAUNTLET] SaveGauntletWave refused for run {row.RunId} wave {row.Wave}: family is longer than {GauntletFamilyMaxLength}.");
                return false;
            }

            try
            {
                using (var context = new ShardDbContext())
                {
                    context.Database.ExecuteSqlRaw(UpsertGauntletWaveSql,
                        new MySqlParameter("@runId", row.RunId),
                        new MySqlParameter("@wave", row.Wave),
                        new MySqlParameter("@level", row.Level),
                        new MySqlParameter("@headcount", row.Headcount),
                        new MySqlParameter("@planned", row.Planned),
                        new MySqlParameter("@killed", row.Killed),
                        new MySqlParameter("@seconds", row.Seconds),
                        new MySqlParameter("@outcome", row.Outcome),
                        new MySqlParameter("@fractionCenti", row.FractionCenti),
                        new MySqlParameter("@playerDeaths", row.PlayerDeaths),
                        new MySqlParameter("@fighterDeaths", row.FighterDeaths),
                        new MySqlParameter("@family", (object)row.Family ?? DBNull.Value));
                }

                return true;
            }
            catch (Exception ex)
            {
                log.Error($"[GAUNTLET] SaveGauntletWave for run {row.RunId} wave {row.Wave} failed: {ex.GetFullMessage()}");
                return false;
            }
        }

        /// <summary>
        /// Writes one Avatar's record with absolute values (<see cref="UpsertGauntletRecordSql"/>), creating the row on
        /// the first write. updated_At is always stamped UTC now; every other timestamp is stored UTC to the second.
        /// Refused for a null row or avatar_Id 0.
        /// </summary>
        /// <returns>true when the row was written; false, logged, when it was refused or the write failed.</returns>
        public virtual bool UpsertGauntletRecord(AllegianceGauntletRecord row)
        {
            if (row == null || row.AvatarId == 0)
            {
                log.Error("[GAUNTLET] UpsertGauntletRecord refused, nothing written: a null row or avatar_Id 0.");
                return false;
            }

            var r = CopyGauntletRecord(row);
            var now = UtcNowToTheSecond();

            try
            {
                using (var context = new ShardDbContext())
                {
                    context.Database.ExecuteSqlRaw(UpsertGauntletRecordSql,
                        new MySqlParameter("@avatarId", r.AvatarId),
                        new MySqlParameter("@bestCenti", r.BestCenti),
                        new MySqlParameter("@bestParticipants", r.BestParticipants),
                        new MySqlParameter("@bestRunId", DbValue(r.BestRunId)),
                        new MySqlParameter("@bestAt", DbValue(r.BestAt)),
                        new MySqlParameter("@lastRunAt", DbValue(r.LastRunAt)),
                        new MySqlParameter("@runCount", r.RunCount),
                        new MySqlParameter("@updatedAt", now));
                }

                return true;
            }
            catch (Exception ex)
            {
                log.Error($"[GAUNTLET] UpsertGauntletRecord for Avatar {r.AvatarId} failed: {ex.GetFullMessage()}");
                return false;
            }
        }

        /// <summary>
        /// Attempts to take <paramref name="accountId"/>'s Gauntlet crate for the UTC day of <paramref name="date"/>,
        /// with a fresh per-attempt claim token. See the token overload for the outcomes.
        /// </summary>
        public GauntletClaimInsertResult TryInsertGauntletDailyClaim(uint accountId, DateTime date, uint runId, uint characterId)
            => TryInsertGauntletDailyClaim(accountId, date, runId, characterId, NewGauntletClaimToken());

        /// <summary>
        /// Attempts to take <paramref name="accountId"/>'s Gauntlet crate for the UTC day of <paramref name="date"/>
        /// (any time of day is dropped). One plain INSERT adjudicated by the (account_Id, claim_Date) PRIMARY KEY, so
        /// the database applies and refuses in a single statement and no transaction is needed.
        ///
        /// On 1062 the holding row is read back and its claim_token compared with <paramref name="claimToken"/>:
        /// the same token means this attempt's own row, committed by an earlier try whose acknowledgement was lost
        /// (<see cref="GauntletClaimInsertResult.OwnRowOnRetry"/>, a success); a different token means another claim
        /// holds the day (<see cref="GauntletClaimInsertResult.AlreadyClaimedToday"/>).
        ///
        /// RESUBMITTING AFTER <see cref="GauntletClaimInsertResult.Failed"/> MUST REUSE THE SAME TOKEN. Failed means
        /// unknown: the first attempt may have committed. A resubmit with the same token then reads back as
        /// OwnRowOnRetry (the claim is the caller's); a resubmit with a NEW token reads back as AlreadyClaimedToday, so
        /// the day is consumed and no crate is given. A genuinely new attempt must use a new token, or it would be
        /// mistaken for its predecessor's retry.
        ///
        /// Refused (<see cref="GauntletClaimInsertResult.Failed"/>, nothing written) for account 0, run 0, character 0,
        /// or a token that is not exactly 32 ASCII characters. Failed also when the read-back fails, or finds no row
        /// (the holder was deleted in between), since this attempt then holds nothing.
        /// </summary>
        public virtual GauntletClaimInsertResult TryInsertGauntletDailyClaim(uint accountId, DateTime date, uint runId, uint characterId, string claimToken)
        {
            if (accountId == 0 || runId == 0 || characterId == 0 || !IsValidGauntletClaimToken(claimToken))
            {
                log.Error($"[GAUNTLET] TryInsertGauntletDailyClaim refused: account {accountId}, run {runId}, character {characterId}; all must be non-zero and the token exactly 32 ASCII characters.");
                return GauntletClaimInsertResult.Failed;
            }

            var day = ToUtcDay(date);

            try
            {
                using (var context = new ShardDbContext())
                {
                    context.Database.ExecuteSqlRaw(InsertGauntletClaimSql,
                        new MySqlParameter("@accountId", accountId),
                        new MySqlParameter("@claimDate", day),
                        new MySqlParameter("@runId", runId),
                        new MySqlParameter("@characterId", characterId),
                        new MySqlParameter("@claimToken", claimToken),
                        new MySqlParameter("@claimedAt", UtcNowToTheSecond()));
                }

                return GauntletClaimInsertResult.Inserted;
            }
            catch (Exception ex) when (IsDuplicateKeyError(ex))
            {
                return ResolveGauntletClaimDuplicate(accountId, day, claimToken);
            }
            catch (Exception ex)
            {
                log.Error($"[GAUNTLET] TryInsertGauntletDailyClaim failed for account {accountId}, day {day:yyyy-MM-dd}, run {runId}: {ex.GetFullMessage()}");
                return GauntletClaimInsertResult.Failed;
            }
        }

        /// <summary>
        /// Deletes the account's claim row for the UTC day of <paramref name="date"/>, but only when it is the row
        /// written for <paramref name="runId"/> and <paramref name="characterId"/>: the undo for a claim whose crate
        /// could not be given, so the day is not consumed. Another run's or character's row for the same day is never
        /// touched. Deleting a row that is not there is a successful no-op with deleted 0.
        /// </summary>
        /// <returns>true with the number of rows deleted (0 or 1); false, logged, when it was refused or FAILED.</returns>
        public virtual bool DeleteGauntletDailyClaim(uint accountId, DateTime date, uint runId, uint characterId, out int deleted)
        {
            deleted = 0;

            if (accountId == 0 || runId == 0 || characterId == 0)
            {
                log.Error($"[GAUNTLET] DeleteGauntletDailyClaim refused: account {accountId}, run {runId}, character {characterId}; all must be non-zero.");
                return false;
            }

            var day = ToUtcDay(date);

            try
            {
                using (var context = new ShardDbContext())
                {
                    deleted = context.AllegianceGauntletClaim
                        .Where(c => c.AccountId == accountId && c.ClaimDate == day && c.RunId == runId && c.CharacterId == characterId)
                        .ExecuteDelete();
                }

                return true;
            }
            catch (Exception ex)
            {
                log.Error($"[GAUNTLET] DeleteGauntletDailyClaim failed for account {accountId}, day {day:yyyy-MM-dd}, run {runId}: {ex.GetFullMessage()}");
                deleted = 0;
                return false;
            }
        }

        // ---- helpers ---------------------------------------------------------------------------------------------

        private GauntletClaimInsertResult ResolveGauntletClaimDuplicate(uint accountId, DateTime day, string claimToken)
        {
            try
            {
                using (var context = new ShardDbContext())
                {
                    var holder = context.AllegianceGauntletClaim
                        .AsNoTracking()
                        .Where(c => c.AccountId == accountId && c.ClaimDate == day)
                        .Select(c => c.ClaimToken)
                        .FirstOrDefault();

                    if (holder == null)
                    {
                        log.Error($"[GAUNTLET] TryInsertGauntletDailyClaim: account {accountId}, day {day:yyyy-MM-dd} reported a duplicate but no row holds it now; nothing is claimed.");
                        return GauntletClaimInsertResult.Failed;
                    }

                    return string.Equals(holder, claimToken, StringComparison.Ordinal)
                        ? GauntletClaimInsertResult.OwnRowOnRetry
                        : GauntletClaimInsertResult.AlreadyClaimedToday;
                }
            }
            catch (Exception ex)
            {
                log.Error($"[GAUNTLET] TryInsertGauntletDailyClaim: the duplicate read-back failed for account {accountId}, day {day:yyyy-MM-dd}: {ex.GetFullMessage()}");
                return GauntletClaimInsertResult.Failed;
            }
        }

        /// <summary>A fresh 32-character lowercase hex token.</summary>
        public static string NewGauntletClaimToken() => Guid.NewGuid().ToString("N");

        private static bool IsValidGauntletClaimToken(string token)
            => token != null && token.Length == 32 && token.All(c => c > ' ' && c < 0x7F);

        /// <summary>Null when the row is acceptable for InsertGauntletRun, else a one-line reason. Pure.</summary>
        internal static string ValidateGauntletRun(AllegianceGauntletRun row)
        {
            if (row == null)
                return "a null row";

            if (row.AvatarId == 0)
                return "avatar_Id 0";

            if (string.IsNullOrEmpty(row.SiteId))
                return $"Avatar {row.AvatarId}: an empty site_Id";

            if (row.SiteId.Length > GauntletSiteIdMaxLength)
                return $"Avatar {row.AvatarId}: site_Id is longer than {GauntletSiteIdMaxLength}";

            return null;
        }

        /// <summary>UTC, truncated to whole seconds (a MySQL `datetime` would ROUND a fraction).</summary>
        internal static DateTime ToUtcSecond(DateTime value)
        {
            var ticks = ShardDatabase.ToUtc(value).Ticks;
            return new DateTime(ticks - ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
        }

        private static DateTime? ToUtcSecondOrNull(DateTime? value) => value.HasValue ? ToUtcSecond(value.Value) : (DateTime?)null;

        /// <summary>A detached copy with every timestamp UTC to the second. A default StartedAt stays default.</summary>
        internal static AllegianceGauntletRun CopyGauntletRun(AllegianceGauntletRun row) => row == null ? null : new AllegianceGauntletRun
        {
            RunId = row.RunId,
            AvatarId = row.AvatarId,
            MonarchGuid = row.MonarchGuid,
            SiteId = row.SiteId,
            StartedAt = row.StartedAt == default ? default : ToUtcSecond(row.StartedAt),
            ActiveAt = ToUtcSecondOrNull(row.ActiveAt),
            EndedAt = ToUtcSecondOrNull(row.EndedAt),
            EndReason = row.EndReason,
            WavesCleared = row.WavesCleared,
            ScoreCenti = row.ScoreCenti,
            Participants = row.Participants,
            Eligible = row.Eligible,
            PeakHeadcount = row.PeakHeadcount,
            FavorPrice = row.FavorPrice,
            FavorCommitted = row.FavorCommitted,
            CratesClaimed = row.CratesClaimed,
            Ranked = row.Ranked,
        };

        /// <summary>A detached copy with every timestamp UTC to the second. UpdatedAt is copied as is; the write stamps its own.</summary>
        internal static AllegianceGauntletRecord CopyGauntletRecord(AllegianceGauntletRecord row) => row == null ? null : new AllegianceGauntletRecord
        {
            AvatarId = row.AvatarId,
            BestCenti = row.BestCenti,
            BestParticipants = row.BestParticipants,
            BestRunId = row.BestRunId,
            BestAt = ToUtcSecondOrNull(row.BestAt),
            LastRunAt = ToUtcSecondOrNull(row.LastRunAt),
            RunCount = row.RunCount,
            UpdatedAt = row.UpdatedAt,
        };

        /// <summary>A detached copy.</summary>
        internal static AllegianceGauntletWave CopyGauntletWave(AllegianceGauntletWave row) => row == null ? null : new AllegianceGauntletWave
        {
            RunId = row.RunId,
            Wave = row.Wave,
            Level = row.Level,
            Headcount = row.Headcount,
            Planned = row.Planned,
            Killed = row.Killed,
            Seconds = row.Seconds,
            Outcome = row.Outcome,
            FractionCenti = row.FractionCenti,
            PlayerDeaths = row.PlayerDeaths,
            FighterDeaths = row.FighterDeaths,
            Family = row.Family,
        };
    }
}
