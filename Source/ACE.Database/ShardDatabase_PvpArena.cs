using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.EntityFrameworkCore;

using ACE.Common.Extensions;
using ACE.Database.Models.Shard;

namespace ACE.Database
{
    /// <summary>
    /// PvP Arena (DreamWeave): the DAO over `character_pvp_rating`, `pvp_match` and
    /// `pvp_match_participant` (Database/Updates/Shard/2026-09-25-02-Add-Pvp-Arena.sql,
    /// Docs/Pvp/DESIGN.md "Storage").
    ///
    /// THE BOUNDARY. Every public member takes or returns the plain records in PvpArenaRecords.cs.
    /// The EF entities are mapped here and nowhere else, so ACE.Server's arena code never holds a
    /// tracked entity or a DbContext.
    ///
    /// ONE CONTEXT PER CALL, ONE SaveChanges(). A resolved match (the match row, its participant rows
    /// and every rating upsert) is written by a single SaveChanges, which is its own implicit
    /// transaction: either all of it lands or none of it does.
    ///
    /// NOTHING IN THIS FILE MAY OPEN A USER-INITIATED EF TRANSACTION. ShardDbContext turns on
    /// EnableRetryOnFailure in OnConfiguring, and with it active MySqlRetryingExecutionStrategy refuses
    /// one outright (see the repo CLAUDE.md). The single SaveChanges above needs none. If some future
    /// need genuinely has to span statements it must go through
    /// context.Database.CreateExecutionStrategy().Execute(...) with an idempotent delegate.
    ///
    /// NOT BIOTA OPERATIONS. Nothing here is virtual and nothing is mirrored on
    /// ShardDatabaseWithCaching, for the same reason character_speed_run and the CAP ledger are not:
    /// these tables have nothing to do with the in-memory biota cache.
    /// </summary>
    public partial class ShardDatabase
    {
        /// <summary>
        /// Every rating row on every ladder. Read once at boot to build the in-memory ladders.
        ///
        /// THREE OUTCOMES, and the difference is load-bearing:
        ///   - a list (possibly empty): the read succeeded.
        ///   - an EMPTY list, logged as a warning: the table does not exist (MySQL error 1146), which
        ///     is the expected state of a deployment whose migration has not run yet, because
        ///     AutoApplyDatabaseUpdates is config-gated. Boot must survive this, and an empty ladder is
        ///     the correct answer: nobody has a rating yet.
        ///   - NULL, logged as an error: the read FAILED for any other reason. A caller must not treat
        ///     this as "nobody has a rating". An empty in-memory ladder seeded from a failed read would
        ///     rate every player from the initial rating, and the next match's upsert would then
        ///     overwrite that player's real stored rating with it.
        /// This method never throws.
        /// </summary>
        public List<PvpRatingRecord> GetAllPvpRatings()
        {
            try
            {
                using (var context = new ShardDbContext())
                {
                    context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                    return context.CharacterPvpRating
                        .ToList()
                        .Select(ToRecord)
                        .ToList();
                }
            }
            catch (Exception ex) when (IsMissingTableError(ex))
            {
                log.Warn($"[PVP] GetAllPvpRatings: the character_pvp_rating table does not exist - every ladder starts empty. Expected until Database/Updates/Shard/2026-09-25-02-Add-Pvp-Arena.sql is applied. {ex.GetFullMessage()}");
                return new List<PvpRatingRecord>();
            }
            catch (Exception ex)
            {
                log.Error($"[PVP] GetAllPvpRatings failed - ratings are UNAVAILABLE, not empty: {ex.GetFullMessage()}");
                return null;
            }
        }

        /// <summary>
        /// Writes one resolved match: inserts the `pvp_match` row and its `pvp_match_participant` rows,
        /// and upserts every `character_pvp_rating` row in <paramref name="ratingUpserts"/>, all in ONE
        /// SaveChanges (its own implicit transaction, so no explicit transaction is opened).
        ///
        /// Rating upserts carry ABSOLUTE values (rating, games, wins, losses, draws, peak, name), never
        /// deltas: an existing row is overwritten field by field and a missing row is inserted. Because
        /// the values are absolute, a retry of the SaveChanges by the execution strategy cannot double
        /// count them. A retry after a commit whose acknowledgement was lost behaves one of two ways.
        /// If the batch inserted a first-time rating row, the retry collides with it and the result is
        /// <see cref="PvpMatchSaveResult.Ambiguous"/>. If every rating row already existed, the retry
        /// succeeds and records the append-only match a second time under a new id. That is the
        /// standard EF retry trade and is left alone: a duplicated history line is harmless, a lost one
        /// is not. <see cref="PvpMatchRecord.Id"/> and <see cref="PvpMatchParticipantRecord.MatchId"/>
        /// are ignored: the database assigns the id and every participant is attached to this match.
        ///
        /// Timestamps: a DateTimeKind.Local value is converted to UTC. An unset EndedAt (default) is
        /// stamped with DateTime.UtcNow, and an unset rating LastMatchAt takes the match's EndedAt,
        /// because none of these columns has a database default to fall back on.
        ///
        /// Refused up front (<see cref="PvpMatchSaveResult.Failed"/>, nothing written): a null match, a
        /// null participant or rating entry, the same character twice among the participants, or the
        /// same (character, ladder) twice among the upserts. Each of those would otherwise fail inside
        /// SaveChanges with a less useful error.
        ///
        /// Never throws. The three results:
        ///   - <see cref="PvpMatchSaveResult.Saved"/>: committed. <paramref name="matchId"/> is the new id.
        ///   - <see cref="PvpMatchSaveResult.Failed"/>: definitely nothing was written (refused up front,
        ///     or SaveChanges failed with anything other than a duplicate key). matchId is 0.
        ///   - <see cref="PvpMatchSaveResult.Ambiguous"/>: SaveChanges failed with a duplicate key
        ///     (MySQL 1062). Validation has already ruled out duplicates inside the batch, and the
        ///     participant rows hang off a freshly generated match id, so the only realistic source is
        ///     a first-time `character_pvp_rating` INSERT colliding with a row that already exists. The
        ///     likely cause is the execution strategy retrying after an attempt that DID commit but lost
        ///     its acknowledgement, so the match may well be recorded already. matchId is 0 because the
        ///     id of any earlier committed attempt is unknown.
        ///
        /// CALLER CONTRACT: the caller must never resubmit the same match on either Failed or Ambiguous.
        /// Resubmitting after Ambiguous could record the match twice. Resubmitting after Failed buys
        /// little, because the execution strategy has already retried transient errors. Log it and move
        /// on. The in-memory ratings stay authoritative and are rewritten in full by that player's next
        /// match.
        /// </summary>
        public PvpMatchSaveResult SavePvpMatchResult(PvpMatchRecord match, IReadOnlyList<PvpMatchParticipantRecord> participants, IReadOnlyList<PvpRatingRecord> ratingUpserts, out uint matchId)
        {
            matchId = 0;

            if (match == null)
            {
                log.Error("[PVP] SavePvpMatchResult called with a null match.");
                return PvpMatchSaveResult.Failed;
            }

            participants ??= Array.Empty<PvpMatchParticipantRecord>();
            ratingUpserts ??= Array.Empty<PvpRatingRecord>();

            var problem = ValidatePvpMatchResult(participants, ratingUpserts);

            if (problem != null)
            {
                log.Error($"[PVP] SavePvpMatchResult refused for a {match.Mode} match on {match.Map}: {problem}");
                return PvpMatchSaveResult.Failed;
            }

            try
            {
                using (var context = new ShardDbContext())
                {
                    var matchEntity = ToEntity(match);

                    foreach (var p in participants)
                        matchEntity.Participants.Add(ToEntity(p));

                    context.PvpMatch.Add(matchEntity);

                    if (ratingUpserts.Count > 0)
                    {
                        var characterIds = ratingUpserts.Select(r => r.CharacterId).Distinct().ToList();
                        var ladders = ratingUpserts.Select(r => r.Ladder).Distinct().ToList();

                        // Tracked on purpose: the rows found here are modified in place and written
                        // by the SaveChanges below. The query is a superset (id IN .. AND ladder IN ..)
                        // and the dictionary picks the exact (character, ladder) pairs out of it.
                        var existing = context.CharacterPvpRating
                            .Where(r => characterIds.Contains(r.CharacterId) && ladders.Contains(r.Ladder))
                            .ToList()
                            .ToDictionary(r => (r.CharacterId, r.Ladder));

                        foreach (var upsert in ratingUpserts)
                        {
                            var lastMatchAt = upsert.LastMatchAt == default ? matchEntity.EndedAt : ToUtc(upsert.LastMatchAt);

                            if (existing.TryGetValue((upsert.CharacterId, upsert.Ladder), out var row))
                                CopyInto(row, upsert, lastMatchAt);
                            else
                            {
                                row = new CharacterPvpRating { CharacterId = upsert.CharacterId, Ladder = upsert.Ladder };
                                CopyInto(row, upsert, lastMatchAt);
                                context.CharacterPvpRating.Add(row);
                            }
                        }
                    }

                    context.SaveChanges();

                    matchId = matchEntity.Id;
                }

                return PvpMatchSaveResult.Saved;
            }
            catch (Exception ex)
            {
                var outcome = ClassifySaveFailure(ex);

                if (outcome == PvpMatchSaveResult.Ambiguous)
                    log.Error($"[PVP] SavePvpMatchResult hit a duplicate key for a {match.Mode} match on {match.Map} ended {match.EndedAt:u} with {participants.Count} participants and {ratingUpserts.Count} rating upserts - OUTCOME AMBIGUOUS, the match may already be recorded by an earlier committed attempt: {ex.GetFullMessage()}");
                else
                    log.Error($"[PVP] SavePvpMatchResult failed for a {match.Mode} match on {match.Map} ended {match.EndedAt:u} with {participants.Count} participants and {ratingUpserts.Count} rating upserts - nothing was written: {ex.GetFullMessage()}");

                matchId = 0;
                return outcome;
            }
        }

        /// <summary>
        /// Null when the batch is acceptable, else a one-line reason. Pure, so it is unit tested
        /// without a database.
        /// </summary>
        internal static string ValidatePvpMatchResult(IReadOnlyList<PvpMatchParticipantRecord> participants, IReadOnlyList<PvpRatingRecord> ratingUpserts)
        {
            if (participants.Any(p => p == null))
                return "a participant entry is null";

            if (ratingUpserts.Any(r => r == null))
                return "a rating upsert entry is null";

            var duplicateParticipant = participants.GroupBy(p => p.CharacterId).FirstOrDefault(g => g.Count() > 1);

            if (duplicateParticipant != null)
                return $"character 0x{duplicateParticipant.Key:X8} appears {duplicateParticipant.Count()} times among the participants";

            if (ratingUpserts.Any(r => string.IsNullOrEmpty(r.Ladder)))
                return "a rating upsert has no ladder";

            var duplicateRating = ratingUpserts.GroupBy(r => (r.CharacterId, r.Ladder)).FirstOrDefault(g => g.Count() > 1);

            if (duplicateRating != null)
                return $"character 0x{duplicateRating.Key.CharacterId:X8} has {duplicateRating.Count()} upserts for ladder {duplicateRating.Key.Ladder}";

            return null;
        }

        /// <summary>
        /// Maps a SaveChanges failure to its result: a duplicate key (MySQL 1062) anywhere in the
        /// exception chain is <see cref="PvpMatchSaveResult.Ambiguous"/>, anything else is
        /// <see cref="PvpMatchSaveResult.Failed"/>. It reuses IsDuplicateKeyError from
        /// ShardDatabase_CharacterSheetLink.cs (the same partial class), which walks the whole chain so
        /// the DbUpdateException or execution-strategy wrapper does not hide the MySQL error. Pure, so
        /// it is unit tested without a database.
        /// </summary>
        internal static PvpMatchSaveResult ClassifySaveFailure(Exception ex)
            => IsDuplicateKeyError(ex) ? PvpMatchSaveResult.Ambiguous : PvpMatchSaveResult.Failed;

        /// <summary>
        /// True when any exception in the chain is MySQL ER_NO_SUCH_TABLE (1146). Walks the whole
        /// chain, as ShardDatabase_CharacterSheetLink.IsDuplicateKeyError does, so a wrapper such as an
        /// execution-strategy exception does not hide it.
        /// </summary>
        internal static bool IsMissingTableError(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (e is MySqlConnector.MySqlException mysql && mysql.ErrorCode == MySqlConnector.MySqlErrorCode.NoSuchTable)
                    return true;
            }

            return false;
        }

        // ---- mapping: the only place the entities and the records meet --------------------------

        internal static DateTime ToUtc(DateTime value)
            => value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);

        internal static PvpRatingRecord ToRecord(CharacterPvpRating row) => new PvpRatingRecord
        {
            CharacterId = row.CharacterId,
            Ladder = row.Ladder,
            CharacterName = row.CharacterName,
            Rating = row.Rating,
            Games = row.Games,
            Wins = row.Wins,
            Losses = row.Losses,
            Draws = row.Draws,
            Peak = row.Peak,
            LastMatchAt = DateTime.SpecifyKind(row.LastMatchAt, DateTimeKind.Utc),
        };

        internal static void CopyInto(CharacterPvpRating row, PvpRatingRecord upsert, DateTime lastMatchAtUtc)
        {
            row.CharacterName = upsert.CharacterName ?? string.Empty;
            row.Rating = upsert.Rating;
            row.Games = upsert.Games;
            row.Wins = upsert.Wins;
            row.Losses = upsert.Losses;
            row.Draws = upsert.Draws;
            row.Peak = upsert.Peak;
            row.LastMatchAt = lastMatchAtUtc;
        }

        internal static PvpMatch ToEntity(PvpMatchRecord match) => new PvpMatch
        {
            Mode = match.Mode,
            Ladder = match.Ladder,
            Map = match.Map,
            StartedAt = match.StartedAt.HasValue ? ToUtc(match.StartedAt.Value) : (DateTime?)null,
            EndedAt = match.EndedAt == default ? DateTime.UtcNow : ToUtc(match.EndedAt),
            Outcome = match.Outcome,
            EndReason = match.EndReason,
            Rated = match.Rated,
        };

        internal static PvpMatchParticipant ToEntity(PvpMatchParticipantRecord p) => new PvpMatchParticipant
        {
            CharacterId = p.CharacterId,
            CharacterName = p.CharacterName ?? string.Empty,
            Team = p.Team,
            Placement = p.Placement,
            Result = p.Result,
            RatingBefore = p.RatingBefore,
            RatingAfter = p.RatingAfter,
            Kills = p.Kills,
            Deaths = p.Deaths,
            ForfeitReason = p.ForfeitReason,
        };
    }
}
