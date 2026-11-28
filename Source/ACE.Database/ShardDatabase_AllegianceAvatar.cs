using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.EntityFrameworkCore;

using MySqlConnector;

using ACE.Common.Extensions;
using ACE.Database.Models.Shard;

namespace ACE.Database
{
    /// <summary>
    /// Allegiance Avatar: the DAO over `allegiance_avatar` and `allegiance_avatar_contribution`
    /// (Database/Updates/Shard/2026-11-20-01-Add-Allegiance-Avatar.sql). The in-memory AvatarManager is the only
    /// caller. It reads both tables once at startup, synchronously through DatabaseManager.Shard.BaseDatabase before
    /// the world ticks, and from then on writes through SerializedShardDatabase's queue.
    ///
    /// ONE CONTEXT PER CALL. Every method opens its own ShardDbContext, LOGS AND SWALLOWS, and never throws.
    ///
    /// ABSOLUTE VALUES, NEVER DELTAS. The manager owns the running totals in memory and hands this layer the values
    /// to store. Every write in SaveAvatarBatch sets absolute values (an UPDATE by avatar_Id for an Avatar, an
    /// INSERT ... ON DUPLICATE KEY UPDATE col = VALUES(col) for a contribution row), so a statement re-run by a
    /// retry, or a whole batch resubmitted after a lost acknowledgement, writes the same values again instead of
    /// adding them twice.
    ///
    /// THE ONE EXPLICIT TRANSACTION in this file (SaveAvatarBatch) is opened inside
    /// context.Database.CreateExecutionStrategy().Execute(...), because ShardDbContext turns on EnableRetryOnFailure
    /// and MySqlRetryingExecutionStrategy refuses a bare BeginTransaction outright (see the repo CLAUDE.md). The
    /// delegate is idempotent for the reason above, and every value it binds (including the timestamp) is computed
    /// before the strategy runs it, so a retry binds the same values as the first attempt.
    ///
    /// TIMESTAMPS ARE UTC AND ALWAYS EXPLICIT. created_At and updated_At carry a CURRENT_TIMESTAMP default, which is
    /// the DATABASE SERVER'S local time, so nothing here relies on it: InsertAvatar sets both, and every
    /// SaveAvatarBatch Avatar UPDATE sets updated_At and never touches created_At. A detached snapshot whose
    /// CreatedAt/UpdatedAt are still default therefore never writes DateTime.MinValue. Day keys (`day`,
    /// `last_Decay_Day`) are always stored and compared as the UTC .Date.
    ///
    /// NOT BIOTA OPERATIONS. Nothing here is virtual and nothing is mirrored on ShardDatabaseWithCaching, for the same
    /// reason the CAP ledger and the PvP tables are not: these tables have nothing to do with the biota cache.
    /// </summary>
    public partial class ShardDatabase
    {
        /// <summary>
        /// Clears the monarch binding of one batch row ahead of the Avatar writes, but only when the batch changes it.
        /// `NOT (a <=> b)` is the NULL-safe "differs": a row already holding its new guid, or already NULL and staying
        /// NULL, is left alone. See SaveAvatarBatch for why this runs before every Avatar write.
        /// </summary>
        private const string ReleaseAvatarMonarchSql =
            "UPDATE `allegiance_avatar` SET `monarch_Guid` = NULL " +
            "WHERE `avatar_Id` = @avatarId AND `monarch_Guid` IS NOT NULL AND NOT (`monarch_Guid` <=> @monarchGuid)";

        /// <summary>
        /// The absolute-value Avatar write. An UPDATE keyed on avatar_Id, deliberately NOT an upsert: under
        /// INSERT ... ON DUPLICATE KEY UPDATE a snapshot whose avatar_Id does not exist, carrying a guid another Avatar
        /// holds, would hit `allegiance_avatar_monarch_uidx` and overwrite THAT other Avatar's row. Every Avatar is
        /// founded by InsertAvatar, so a missing row is a caller bug and fails the batch (see SaveAvatarBatch).
        /// created_At is never touched. Internal so a DB-free test can pin that it names every mapped column: a column
        /// added to the entity but not here is silently never written.
        /// </summary>
        internal const string UpdateAvatarSql =
            "UPDATE `allegiance_avatar` SET " +
            "`monarch_Guid` = @monarchGuid, " +
            "`state` = @state, " +
            "`lifetime_Xp` = @lifetimeXp, " +
            "`level_Xp` = @levelXp, " +
            "`favor` = @favor, " +
            "`heir_Guid` = @heirGuid, " +
            "`seat_House_Guid` = @seatHouseGuid, " +
            "`seat_Cell` = @seatCell, " +
            "`seat_Instance` = @seatInstance, " +
            "`seat_X` = @seatX, " +
            "`seat_Y` = @seatY, " +
            "`seat_Z` = @seatZ, " +
            "`seat_Qw` = @seatQw, " +
            "`seat_Qx` = @seatQx, " +
            "`seat_Qy` = @seatQy, " +
            "`seat_Qz` = @seatQz, " +
            "`seated_At` = @seatedAt, " +
            "`absorbed_Into_Id` = @absorbedIntoId, " +
            "`last_Contribution_At` = @lastContributionAt, " +
            "`last_Decay_Day` = @lastDecayDay, " +
            "`look_Json` = @lookJson, " +
            "`blessing_Bought_At` = @blessingBoughtAt, " +
            "`last_Event_At` = @lastEventAt, " +
            "`events_On_Day` = @eventsOnDay, " +
            "`updated_At` = @updatedAt " +
            "WHERE `avatar_Id` = @avatarId";

        /// <summary>The absolute-value contribution upsert, keyed on the (avatar_Id, account_Id, day) PRIMARY KEY.</summary>
        private const string UpsertAvatarContributionSql =
            "INSERT INTO `allegiance_avatar_contribution` " +
            "(`avatar_Id`, `account_Id`, `day`, `raw_Xp`, `credited`, `last_Character_Id`) " +
            "VALUES (@avatarId, @accountId, @day, @rawXp, @credited, @lastCharacterId) " +
            "ON DUPLICATE KEY UPDATE " +
            "`raw_Xp` = VALUES(`raw_Xp`), " +
            "`credited` = VALUES(`credited`), " +
            "`last_Character_Id` = VALUES(`last_Character_Id`)";

        /// <summary>
        /// Every Avatar row, in any state. Read once at startup. Returns an empty list when there are none and NULL
        /// when the read FAILED (including a missing table). A caller must not treat NULL as "no Avatars": seeding an
        /// empty manager from a failed read would let the next save found duplicates of Avatars that already exist.
        /// </summary>
        public List<AllegianceAvatar> GetAllAvatars()
        {
            try
            {
                using (var context = new ShardDbContext())
                {
                    context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                    return context.AllegianceAvatar.ToList();
                }
            }
            catch (Exception ex)
            {
                log.Error($"[AVATAR] GetAllAvatars failed - Avatars are UNAVAILABLE, not empty: {ex.GetFullMessage()}");
                return null;
            }
        }

        /// <summary>
        /// Every contribution row whose day is on or after the UTC date of <paramref name="dayUtc"/> (any time of day
        /// is dropped, so the whole of that day is included). Used at startup for today's caps and the 30-day top
        /// list. Returns an empty list when there are none and NULL when the read FAILED.
        /// </summary>
        public List<AllegianceAvatarContribution> GetAvatarContributionsSince(DateTime dayUtc)
        {
            var since = ToUtcDay(dayUtc);

            try
            {
                using (var context = new ShardDbContext())
                {
                    context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                    return context.AllegianceAvatarContribution
                        .Where(c => c.Day >= since)
                        .ToList();
                }
            }
            catch (Exception ex)
            {
                log.Error($"[AVATAR] GetAvatarContributionsSince({since:yyyy-MM-dd}) failed - contributions are UNAVAILABLE, not empty: {ex.GetFullMessage()}");
                return null;
            }
        }

        /// <summary>
        /// Writes one flush of Avatar state and contribution rows in ONE transaction: all of it lands or none of it
        /// does. Every row is written with absolute values (see the class header), so the batch is safe to retry.
        ///
        /// AVATARS MUST EXIST. Each Avatar is an UPDATE by avatar_Id (<see cref="UpdateAvatarSql"/>), and the delegate
        /// throws unless it reports exactly 1 row, so an unknown avatar_Id rolls the whole batch back and returns
        /// false. That count is FOUND rows, not changed rows: MySqlConnector's UseAffectedRows defaults to false and
        /// ShardDbContext never sets it, so an unchanged re-run of the same snapshot still reports 1.
        ///
        /// MONARCH HANDOVER. monarch_Guid is UNIQUE (`allegiance_avatar_monarch_uidx`), and InnoDB checks it per
        /// statement, not at commit. If one Avatar releases guid G (to NULL, or by moving to another guid) and another
        /// takes G in the same batch, writing the taker first would collide with the releaser's still-stored G. So
        /// before any Avatar write, every batch Avatar whose stored monarch_Guid differs from its new one has its binding
        /// cleared (<see cref="ReleaseAvatarMonarchSql"/>). Every release is therefore written before every take,
        /// whatever the input order, and a swap (A takes B's guid while B takes A's) works too, which no ordering of
        /// whole-row writes could achieve. A guid still bound to an Avatar OUTSIDE the batch is a genuine conflict:
        /// its UPDATE fails with a duplicate key and the whole transaction, pre-clears included, rolls back.
        ///
        /// Refused up front (false, nothing written): a null entry, an avatar_Id of 0 (every Avatar comes from
        /// <see cref="InsertAvatar"/> first), the same avatar_Id twice, the same non-NULL monarch_Guid twice, or the
        /// same (avatar_Id, account_Id, UTC day) twice. Two empty lists are a successful no-op.
        ///
        /// The entities passed in are read, never modified, and none is attached to a context.
        /// </summary>
        /// <returns>true when the batch committed; false, logged, when it was refused or failed.</returns>
        public bool SaveAvatarBatch(IReadOnlyList<AllegianceAvatar> avatars, IReadOnlyList<AllegianceAvatarContribution> rows)
        {
            avatars ??= Array.Empty<AllegianceAvatar>();
            rows ??= Array.Empty<AllegianceAvatarContribution>();

            var problem = ValidateAvatarBatch(avatars, rows);

            if (problem != null)
            {
                log.Error($"[AVATAR] SaveAvatarBatch refused, nothing written ({avatars.Count} Avatars, {rows.Count} contribution rows): {problem}");
                return false;
            }

            if (avatars.Count == 0 && rows.Count == 0)
                return true;

            // Computed once, outside the strategy, so a retried attempt binds exactly the same values.
            var now = UtcNowToTheSecond();

            try
            {
                using (var context = new ShardDbContext())
                {
                    var strategy = context.Database.CreateExecutionStrategy();

                    strategy.Execute(() =>
                    {
                        using (var transaction = context.Database.BeginTransaction())
                        {
                            foreach (var avatar in avatars)
                            {
                                context.Database.ExecuteSqlRaw(
                                    ReleaseAvatarMonarchSql,
                                    new MySqlParameter("@avatarId", avatar.AvatarId),
                                    new MySqlParameter("@monarchGuid", (object)avatar.MonarchGuid ?? DBNull.Value));
                            }

                            foreach (var avatar in avatars)
                            {
                                var found = context.Database.ExecuteSqlRaw(UpdateAvatarSql, AvatarParameters(avatar, now));

                                // Not transient, so the strategy does not retry it. The throw leaves the using block with the
                                // transaction uncommitted, and disposing it there rolls back every statement above.
                                if (found != 1)
                                    throw new InvalidOperationException($"avatar_Id {avatar.AvatarId} matched {found} rows, expected 1; every Avatar must be founded through InsertAvatar first");
                            }

                            foreach (var row in rows)
                            {
                                context.Database.ExecuteSqlRaw(
                                    UpsertAvatarContributionSql,
                                    new MySqlParameter("@avatarId", row.AvatarId),
                                    new MySqlParameter("@accountId", row.AccountId),
                                    new MySqlParameter("@day", ToUtcDay(row.Day)),
                                    new MySqlParameter("@rawXp", row.RawXp),
                                    new MySqlParameter("@credited", row.Credited),
                                    new MySqlParameter("@lastCharacterId", row.LastCharacterId));
                            }

                            transaction.Commit();
                        }
                    });
                }

                return true;
            }
            catch (Exception ex)
            {
                log.Error($"[AVATAR] SaveAvatarBatch failed, nothing written ({avatars.Count} Avatars [{string.Join(", ", avatars.Select(a => a.AvatarId))}], {rows.Count} contribution rows): {ex.GetFullMessage()}");
                return false;
            }
        }

        /// <summary>
        /// Founds one Avatar and returns the avatar_Id the database assigned. The caller's row is copied, never
        /// modified or attached: its AvatarId is ignored, created_At and updated_At are both stamped UTC now (a
        /// non-default CreatedAt is kept, converted to UTC), and the day key is stored as its UTC date.
        ///
        /// A second row for a monarch_Guid that is already bound fails on `allegiance_avatar_monarch_uidx` and returns
        /// false with avatarId 0. Note the retry trade: the INSERT is one SaveChanges under the retrying strategy, so
        /// if a commit's acknowledgement is lost the retry collides with the row it just committed and this returns
        /// false although the Avatar exists. A caller that sees false must reload rather than found it again.
        /// </summary>
        /// <returns>true with the new id; false, logged, with avatarId 0.</returns>
        public bool InsertAvatar(AllegianceAvatar row, out uint avatarId)
        {
            avatarId = 0;

            if (row == null)
            {
                log.Error("[AVATAR] InsertAvatar called with a null row.");
                return false;
            }

            var now = UtcNowToTheSecond();

            var entity = CopyAvatarForWrite(row, now);
            entity.AvatarId = 0;

            try
            {
                using (var context = new ShardDbContext())
                {
                    context.AllegianceAvatar.Add(entity);
                    context.SaveChanges();
                }

                avatarId = entity.AvatarId;
                return true;
            }
            catch (Exception ex)
            {
                var reason = IsDuplicateKeyError(ex) ? "monarch guid already bound to an Avatar" : "insert failed";
                log.Error($"[AVATAR] InsertAvatar for monarch {(row.MonarchGuid.HasValue ? $"0x{row.MonarchGuid.Value:X8}" : "NULL")} failed ({reason}): {ex.GetFullMessage()}");
                return false;
            }
        }

        /// <summary>
        /// Null when the batch is acceptable, else a one-line reason. Pure, so it is unit tested without a database.
        /// </summary>
        internal static string ValidateAvatarBatch(IReadOnlyList<AllegianceAvatar> avatars, IReadOnlyList<AllegianceAvatarContribution> rows)
        {
            if (avatars.Any(a => a == null))
                return "an Avatar entry is null";

            if (rows.Any(r => r == null))
                return "a contribution entry is null";

            var unassigned = avatars.FirstOrDefault(a => a.AvatarId == 0);

            if (unassigned != null)
                return "an Avatar has avatar_Id 0; every Avatar must be founded through InsertAvatar first";

            var duplicateAvatar = avatars.GroupBy(a => a.AvatarId).FirstOrDefault(g => g.Count() > 1);

            if (duplicateAvatar != null)
                return $"avatar_Id {duplicateAvatar.Key} appears {duplicateAvatar.Count()} times";

            var duplicateMonarch = avatars.Where(a => a.MonarchGuid.HasValue).GroupBy(a => a.MonarchGuid.Value).FirstOrDefault(g => g.Count() > 1);

            if (duplicateMonarch != null)
                return $"monarch 0x{duplicateMonarch.Key:X8} is bound to {duplicateMonarch.Count()} Avatars";

            var duplicateRow = rows.GroupBy(r => (r.AvatarId, r.AccountId, Day: ToUtcDay(r.Day))).FirstOrDefault(g => g.Count() > 1);

            if (duplicateRow != null)
                return $"avatar_Id {duplicateRow.Key.AvatarId}, account {duplicateRow.Key.AccountId}, day {duplicateRow.Key.Day:yyyy-MM-dd} appears {duplicateRow.Count()} times";

            return null;
        }

        // ---- helpers ---------------------------------------------------------------------------------------------

        /// <summary>The UTC calendar date of a value: a DateTimeKind.Local value is converted first.</summary>
        internal static DateTime ToUtcDay(DateTime value) => ShardDatabase.ToUtc(value).Date;

        /// <summary>UTC now truncated to whole seconds, the precision of a MySQL `datetime` column.</summary>
        private static DateTime UtcNowToTheSecond()
        {
            var ticks = DateTime.UtcNow.Ticks;
            return new DateTime(ticks - ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
        }

        private static DateTime? ToUtcOrNull(DateTime? value) => value.HasValue ? ShardDatabase.ToUtc(value.Value) : (DateTime?)null;

        private static object DbValue<T>(T? value) where T : struct => value.HasValue ? value.Value : DBNull.Value;

        private static object DbValue(string value) => (object)value ?? DBNull.Value;

        /// <summary>A detached copy with every timestamp normalised to UTC and updated_At set to <paramref name="now"/>.</summary>
        private static AllegianceAvatar CopyAvatarForWrite(AllegianceAvatar row, DateTime now) => new AllegianceAvatar
        {
            AvatarId = row.AvatarId,
            MonarchGuid = row.MonarchGuid,
            State = row.State,
            LifetimeXp = row.LifetimeXp,
            LevelXp = row.LevelXp,
            Favor = row.Favor,
            HeirGuid = row.HeirGuid,
            SeatHouseGuid = row.SeatHouseGuid,
            SeatCell = row.SeatCell,
            SeatInstance = row.SeatInstance,
            SeatX = row.SeatX,
            SeatY = row.SeatY,
            SeatZ = row.SeatZ,
            SeatQw = row.SeatQw,
            SeatQx = row.SeatQx,
            SeatQy = row.SeatQy,
            SeatQz = row.SeatQz,
            SeatedAt = ToUtcOrNull(row.SeatedAt),
            AbsorbedIntoId = row.AbsorbedIntoId,
            LastContributionAt = ToUtcOrNull(row.LastContributionAt),
            LastDecayDay = row.LastDecayDay.HasValue ? ToUtcDay(row.LastDecayDay.Value) : (DateTime?)null,
            LookJson = row.LookJson,
            BlessingBoughtAt = ToUtcOrNull(row.BlessingBoughtAt),
            LastEventAt = ToUtcOrNull(row.LastEventAt),
            EventsOnDay = row.EventsOnDay,
            CreatedAt = row.CreatedAt == default ? now : ShardDatabase.ToUtc(row.CreatedAt),
            UpdatedAt = now,
        };

        internal static object[] AvatarParameters(AllegianceAvatar row, DateTime now)
        {
            var a = CopyAvatarForWrite(row, now);

            return new object[]
            {
                new MySqlParameter("@avatarId", a.AvatarId),
                new MySqlParameter("@monarchGuid", DbValue(a.MonarchGuid)),
                new MySqlParameter("@state", a.State),
                new MySqlParameter("@lifetimeXp", a.LifetimeXp),
                new MySqlParameter("@levelXp", a.LevelXp),
                new MySqlParameter("@favor", a.Favor),
                new MySqlParameter("@heirGuid", DbValue(a.HeirGuid)),
                new MySqlParameter("@seatHouseGuid", DbValue(a.SeatHouseGuid)),
                new MySqlParameter("@seatCell", DbValue(a.SeatCell)),
                new MySqlParameter("@seatInstance", DbValue(a.SeatInstance)),
                new MySqlParameter("@seatX", DbValue(a.SeatX)),
                new MySqlParameter("@seatY", DbValue(a.SeatY)),
                new MySqlParameter("@seatZ", DbValue(a.SeatZ)),
                new MySqlParameter("@seatQw", DbValue(a.SeatQw)),
                new MySqlParameter("@seatQx", DbValue(a.SeatQx)),
                new MySqlParameter("@seatQy", DbValue(a.SeatQy)),
                new MySqlParameter("@seatQz", DbValue(a.SeatQz)),
                new MySqlParameter("@seatedAt", DbValue(a.SeatedAt)),
                new MySqlParameter("@absorbedIntoId", DbValue(a.AbsorbedIntoId)),
                new MySqlParameter("@lastContributionAt", DbValue(a.LastContributionAt)),
                new MySqlParameter("@lastDecayDay", DbValue(a.LastDecayDay)),
                new MySqlParameter("@lookJson", DbValue(a.LookJson)),
                new MySqlParameter("@blessingBoughtAt", DbValue(a.BlessingBoughtAt)),
                new MySqlParameter("@lastEventAt", DbValue(a.LastEventAt)),
                new MySqlParameter("@eventsOnDay", a.EventsOnDay),
                new MySqlParameter("@updatedAt", a.UpdatedAt),
            };
        }
    }
}
