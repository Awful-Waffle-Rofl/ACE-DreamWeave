using System;
using System.Linq;

using Microsoft.EntityFrameworkCore;

using ACE.Common.Extensions;
using ACE.Database.Models.Shard;

namespace ACE.Database
{
    /// <summary>
    /// Character Sheet link DAO (Docs/CharacterSheet/DESIGN.md section 4.1), off the SerializedShardDatabase
    /// worker thread: every call opens its own ShardDbContext, as ShardDatabase_Market.cs does.
    ///
    /// No explicit transaction anywhere. A bare SaveChanges / ExecuteDelete is its own implicit
    /// transaction, and an explicit one would be refused outright by MySqlRetryingExecutionStrategy
    /// (see the repo CLAUDE.md).
    /// </summary>
    public partial class ShardDatabase
    {
        /// <summary>
        /// The link carrying <paramref name="slug"/>. FALSE means the read FAILED; TRUE with a null
        /// <paramref name="row"/> means no such slug.
        /// </summary>
        public bool TryGetCharacterSheetLinkBySlug(string slug, out CharacterSheetLink row)
        {
            row = null;

            if (string.IsNullOrEmpty(slug))
                return true;

            try
            {
                using (var context = new ShardDbContext())
                {
                    context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                    row = context.CharacterSheetLink.FirstOrDefault(l => l.Slug == slug);
                }

                return true;
            }
            catch (Exception ex)
            {
                log.Error($"[CHARSHEET] TryGetCharacterSheetLinkBySlug failed: {ex.GetFullMessage()}");
                row = null;
                return false;
            }
        }

        /// <summary>
        /// The link for <paramref name="characterId"/>. FALSE means the read FAILED; TRUE with a null
        /// <paramref name="row"/> means the character has no public sheet.
        /// </summary>
        public bool TryGetCharacterSheetLinkByCharacter(uint characterId, out CharacterSheetLink row)
        {
            row = null;

            try
            {
                using (var context = new ShardDbContext())
                {
                    context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                    row = context.CharacterSheetLink.FirstOrDefault(l => l.CharacterId == characterId);
                }

                return true;
            }
            catch (Exception ex)
            {
                log.Error($"[CHARSHEET] TryGetCharacterSheetLinkByCharacter failed for character {characterId}: {ex.GetFullMessage()}");
                row = null;
                return false;
            }
        }

        /// <summary>
        /// Inserts the character's link, or rotates an existing one onto <c>row.Slug</c>. CreatedAt is
        /// stamped UTC now on both paths and written back onto <paramref name="row"/> on success.
        ///
        /// FALSE means nothing was written. <paramref name="slugCollision"/> is TRUE when the refusal was
        /// MySQL 1062 (duplicate key): the caller should draw a fresh slug and try again. The caller's
        /// row object is never attached to a context.
        /// </summary>
        public bool UpsertCharacterSheetLink(CharacterSheetLink row, out bool slugCollision)
        {
            slugCollision = false;

            if (row == null || row.CharacterId == 0 || string.IsNullOrEmpty(row.Slug))
            {
                log.Error("[CHARSHEET] UpsertCharacterSheetLink called with a null row, character 0, or an empty slug.");
                return false;
            }

            var characterId = row.CharacterId;
            var slug = row.Slug;
            var now = DateTime.UtcNow;

            try
            {
                using (var context = new ShardDbContext())
                {
                    // Tracked on purpose: the update path mutates this instance and SaveChanges emits the UPDATE.
                    var existing = context.CharacterSheetLink.FirstOrDefault(l => l.CharacterId == characterId);

                    if (existing == null)
                    {
                        context.CharacterSheetLink.Add(new CharacterSheetLink
                        {
                            CharacterId = characterId,
                            Slug = slug,
                            CreatedAt = now,
                        });
                    }
                    else
                    {
                        existing.Slug = slug;
                        existing.CreatedAt = now;
                    }

                    context.SaveChanges();
                }

                row.CreatedAt = now;
                return true;
            }
            catch (Exception ex) when (IsDuplicateKeyError(ex))
            {
                slugCollision = true;
                log.Warn($"[CHARSHEET] UpsertCharacterSheetLink refused a duplicate key for character {characterId}: {ex.GetFullMessage()}");
                return false;
            }
            catch (Exception ex)
            {
                log.Error($"[CHARSHEET] UpsertCharacterSheetLink failed for character {characterId}: {ex.GetFullMessage()}");
                return false;
            }
        }

        /// <summary>Removes the character's link (sheet goes private). TRUE when the row is gone, including when there was none.</summary>
        public bool DeleteCharacterSheetLink(uint characterId)
        {
            try
            {
                using (var context = new ShardDbContext())
                {
                    context.CharacterSheetLink.Where(l => l.CharacterId == characterId).ExecuteDelete();
                }

                return true;
            }
            catch (Exception ex)
            {
                log.Error($"[CHARSHEET] DeleteCharacterSheetLink failed for character {characterId}: {ex.GetFullMessage()}");
                return false;
            }
        }

        /// <summary>
        /// True when any exception in the chain is MySQL ER_DUP_ENTRY (1062). Pomelo surfaces it as the
        /// inner exception of a DbUpdateException; walking the whole chain also covers a wrapper such as
        /// an execution-strategy exception, without depending on which one it is.
        /// </summary>
        private static bool IsDuplicateKeyError(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (e is MySqlConnector.MySqlException mysql && mysql.ErrorCode == MySqlConnector.MySqlErrorCode.DuplicateKeyEntry)
                    return true;
            }

            return false;
        }
    }
}
